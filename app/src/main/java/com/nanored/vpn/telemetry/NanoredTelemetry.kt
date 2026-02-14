package com.nanored.vpn.telemetry

import android.content.Context
import android.content.SharedPreferences
import android.content.pm.PackageManager
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.net.TrafficStats
import android.net.wifi.WifiManager
import android.os.BatteryManager
import android.os.Build
import android.provider.Settings
import android.telephony.TelephonyManager
import android.util.Log
import com.nanored.vpn.AppConfig
import com.nanored.vpn.handler.MmkvManager
import kotlinx.coroutines.*
import kotlinx.coroutines.runBlocking
import org.json.JSONArray
import org.json.JSONObject
import java.io.BufferedReader
import java.io.InputStreamReader
import java.io.OutputStreamWriter
import java.net.URL
import java.util.*
import java.util.concurrent.ConcurrentLinkedQueue
import javax.net.ssl.HttpsURLConnection

object NanoredTelemetry {

    private const val TAG = "NanoredTelemetry"
    private const val PREFS_NAME = "nanored_telemetry"
    private const val KEY_DEVICE_ID = "device_id"
    private const val KEY_API_KEY = "api_key"
    private const val KEY_LAST_ACCOUNT_ID = "last_account_id"

    private lateinit var context: Context
    private lateinit var baseUrl: String
    private var deviceId: String? = null
    private var apiKey: String? = null
    var currentSessionId: String? = null
        private set

    private val appTrafficBuffer = ConcurrentLinkedQueue<AppTrafficEntry>()
    private val connectionBuffer = ConcurrentLinkedQueue<ConnectionEntry>()

    private val scope = CoroutineScope(Dispatchers.IO + SupervisorJob())
    private var heartbeatJob: Job? = null
    private var dnsFlushJob: Job? = null
    private var appTrafficJob: Job? = null

    // Per-UID baseline snapshots for delta calculation
    private val uidRxBaseline = mutableMapOf<Int, Long>()
    private val uidTxBaseline = mutableMapOf<Int, Long>()
    // UID -> (packageName, appLabel)
    private var trackedApps = emptyMap<Int, Pair<String, String?>>()

    data class AppTrafficEntry(val packageName: String, val appName: String? = null, val bytesDown: Long = 0, val bytesUp: Long = 0)
    data class ConnectionEntry(val destIp: String, val destPort: Int, val protocol: String = "TCP", val domain: String? = null)

    fun init(ctx: Context, apiBaseUrl: String) {
        context = ctx.applicationContext
        baseUrl = apiBaseUrl.trimEnd('/')

        val prefs = getPrefs()
        deviceId = prefs.getString(KEY_DEVICE_ID, null)
        apiKey = prefs.getString(KEY_API_KEY, null)

        scope.launch {
            if (apiKey == null) register()
        }

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
                scope.launch { register() }
            }
        }
    }

    private suspend fun register() {
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
                if (apiKey == null) register()
                // Re-register if account_id changed since last registration
                val currentAccountId = getPrefs().getString("account_id", null)
                val lastAccountId = getPrefs().getString(KEY_LAST_ACCOUNT_ID, null)
                if (currentAccountId != lastAccountId && !currentAccountId.isNullOrEmpty()) {
                    register()
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
                // Wait for any in-flight app traffic flush
                appTrafficJob?.cancelAndJoin()
                appTrafficJob = null
                // Collect final app traffic deltas
                collectAppTrafficDeltas()

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
                flushAppTraffic(sessionId)
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
                // Clean up tracking state
                uidRxBaseline.clear()
                uidTxBaseline.clear()
                trackedApps = emptyMap()
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

    /**
     * Start per-app traffic tracking using TrafficStats.
     * Enumerates apps going through VPN and tracks per-UID byte deltas every 30 seconds.
     */
    fun startAppTrafficTracking(ctx: Context) {
        if (appTrafficJob != null) return
        // Build list of tracked UIDs
        trackedApps = buildTrackedAppsMap(ctx)
        if (trackedApps.isEmpty()) return
        // Take initial baseline snapshot
        trackedApps.keys.forEach { uid ->
            uidRxBaseline[uid] = TrafficStats.getUidRxBytes(uid).coerceAtLeast(0)
            uidTxBaseline[uid] = TrafficStats.getUidTxBytes(uid).coerceAtLeast(0)
        }
        appTrafficJob = scope.launch {
            while (isActive && currentSessionId != null) {
                delay(30_000)
                val sid = currentSessionId ?: break
                try {
                    collectAppTrafficDeltas()
                    flushAppTraffic(sid)
                } catch (e: Exception) {
                    Log.e(TAG, "App traffic flush failed", e)
                }
            }
        }
        Log.d(TAG, "App traffic tracking started for ${trackedApps.size} apps")
    }

    private fun stopAppTrafficTracking() {
        appTrafficJob?.cancel()
        appTrafficJob = null
        uidRxBaseline.clear()
        uidTxBaseline.clear()
        trackedApps = emptyMap()
    }

    /**
     * Build a map of UID -> (packageName, appLabel) for apps going through VPN.
     */
    private fun buildTrackedAppsMap(ctx: Context): Map<Int, Pair<String, String?>> {
        val pm = ctx.packageManager
        val result = mutableMapOf<Int, Pair<String, String?>>()
        val perAppEnabled = MmkvManager.decodeSettingsBool(AppConfig.PREF_PER_APP_PROXY)
        if (perAppEnabled) {
            // Track only selected apps
            val selectedApps = MmkvManager.decodeSettingsStringSet(AppConfig.PREF_PER_APP_PROXY_SET)
            val bypassMode = MmkvManager.decodeSettingsBool(AppConfig.PREF_BYPASS_APPS)
            if (bypassMode) {
                // Bypass mode: all apps EXCEPT selected ones go through VPN
                val excludeSet = selectedApps?.toSet() ?: emptySet()
                val installed = pm.getInstalledPackages(0)
                installed.forEach { pkg ->
                    if (pkg.packageName !in excludeSet) {
                        val uid = pkg.applicationInfo?.uid ?: return@forEach
                        val label = try { pm.getApplicationLabel(pkg.applicationInfo!!).toString() } catch (_: Exception) { null }
                        result[uid] = Pair(pkg.packageName, label)
                    }
                }
            } else {
                // Proxy mode: only selected apps go through VPN
                selectedApps?.forEach { pkgName ->
                    try {
                        val appInfo = pm.getApplicationInfo(pkgName, 0)
                        val label = try { pm.getApplicationLabel(appInfo).toString() } catch (_: Exception) { null }
                        result[appInfo.uid] = Pair(pkgName, label)
                    } catch (_: PackageManager.NameNotFoundException) {}
                }
            }
        } else {
            // All apps go through VPN — track apps with INTERNET permission
            val installed = pm.getInstalledPackages(PackageManager.GET_PERMISSIONS)
            installed.forEach { pkg ->
                val perms = pkg.requestedPermissions
                if (perms != null && perms.contains(android.Manifest.permission.INTERNET)) {
                    val uid = pkg.applicationInfo?.uid ?: return@forEach
                    val label = try { pm.getApplicationLabel(pkg.applicationInfo!!).toString() } catch (_: Exception) { null }
                    result[uid] = Pair(pkg.packageName, label)
                }
            }
        }
        return result
    }

    /**
     * Calculate traffic deltas for each tracked UID and add to buffer.
     */
    private fun collectAppTrafficDeltas() {
        trackedApps.forEach { (uid, info) ->
            val currentRx = TrafficStats.getUidRxBytes(uid).coerceAtLeast(0)
            val currentTx = TrafficStats.getUidTxBytes(uid).coerceAtLeast(0)
            val baseRx = uidRxBaseline[uid] ?: 0
            val baseTx = uidTxBaseline[uid] ?: 0
            val deltaRx = (currentRx - baseRx).coerceAtLeast(0)
            val deltaTx = (currentTx - baseTx).coerceAtLeast(0)
            if (deltaRx > 0 || deltaTx > 0) {
                addAppTraffic(info.first, info.second, deltaRx, deltaTx)
            }
            uidRxBaseline[uid] = currentRx
            uidTxBaseline[uid] = currentTx
        }
    }

    private fun processCommands(commands: JSONArray?) {
        if (commands == null || commands.length() == 0) return
        for (i in 0 until commands.length()) {
            val cmd = commands.optJSONObject(i) ?: continue
            when (cmd.optString("type")) {
                "upload_logs" -> collectAndSendLogcat()
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

    fun addAppTraffic(packageName: String, appName: String? = null, bytesDown: Long = 0, bytesUp: Long = 0) { appTrafficBuffer.add(AppTrafficEntry(packageName, appName, bytesDown, bytesUp)) }

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
                    val granted = (requestedFlags[i] and android.content.pm.PackageInfo.REQUESTED_PERMISSION_GRANTED) != 0
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
                if (apiKey == null) register()
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


    private suspend fun flushAppTraffic(sessionId: String) {
        val entries = drainBuffer(appTrafficBuffer); if (entries.isEmpty()) return
        val arr = JSONArray(); entries.forEach { e -> arr.put(JSONObject().apply { put("package_name", e.packageName); put("app_name", e.appName); put("bytes_downloaded", e.bytesDown); put("bytes_uploaded", e.bytesUp) }) }
        post("/api/v1/client/app-traffic/batch", JSONObject().apply { put("session_id", sessionId); put("entries", arr) }, auth = true)
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
                runBlocking { register() }
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
}
