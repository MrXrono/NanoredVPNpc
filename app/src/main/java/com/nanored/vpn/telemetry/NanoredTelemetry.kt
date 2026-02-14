package com.nanored.vpn.telemetry

import android.content.Context
import android.content.SharedPreferences
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.net.wifi.WifiManager
import android.os.BatteryManager
import android.os.Build
import android.provider.Settings
import android.telephony.TelephonyManager
import android.util.Log
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
        // Stop heartbeat and DNS flush immediately
        stopHeartbeat()
        stopDnsFlush()
        currentSessionId = null
        runBlocking {
            try {
                // Final DNS flush
                flushDnsAndSni(sessionId)
                // Send xray access log only on disconnect (via /sni/raw for parsing)
                val rawLog = AccessLogParser.drainRawLog()
                if (rawLog.isNotEmpty()) {
                    // Send to /sni/raw for server-side SNI parsing
                    post("/api/v1/client/sni/raw", JSONObject().apply {
                        put("session_id", sessionId)
                        put("raw_log", rawLog)
                        put("dns_log", "")
                    }, auth = true)
                    Log.d(TAG, "Xray log sent via sni/raw: ${rawLog.length} chars")
                    // Also send as device log so it's visible in admin panel
                    val logBody = JSONObject().apply {
                        put("log_type", "xray_access")
                        put("content", rawLog)
                        put("app_version", getAppVersion())
                    }
                    post("/api/v1/client/logs", logBody, auth = true)
                    Log.d(TAG, "Xray log sent as device log: ${rawLog.length} chars")
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
                    sendDeviceLog("logcat", logcat)
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
