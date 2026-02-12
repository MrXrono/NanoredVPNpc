package com.nanored.vpn.handler

import android.content.Context
import android.os.Build
import android.util.Log
import com.nanored.vpn.AppConfig
import com.nanored.vpn.BuildConfig
import com.nanored.vpn.util.JsonUtil
import java.io.File
import java.io.PrintWriter
import java.io.StringWriter
import java.net.HttpURLConnection
import java.net.URL
import com.nanored.vpn.telemetry.NanoredTelemetry

object CrashReportManager {

    private const val CRASH_ENDPOINT = "https://nanored.top/crash"
    private const val CRASH_DIR = "crash_reports"
    private const val TIMEOUT_MS = 10_000

    private lateinit var appContext: Context
    private var defaultHandler: Thread.UncaughtExceptionHandler? = null

    fun init(context: Context) {
        appContext = context.applicationContext
        defaultHandler = Thread.getDefaultUncaughtExceptionHandler()
        Thread.setDefaultUncaughtExceptionHandler { thread, throwable ->
            handleCrash(thread, throwable)
        }
        retrySavedReports()
    }

    private fun handleCrash(thread: Thread, throwable: Throwable) {
        try {
            // Report crash via telemetry API
            try {
                val sw2 = StringWriter()
                throwable.printStackTrace(PrintWriter(sw2))
                NanoredTelemetry.reportError("crash", throwable.message, sw2.toString())
            } catch (_: Exception) {}

            val report = buildReport(thread, throwable)
            val json = JsonUtil.toJson(report)
            if (!sendReport(json)) {
                saveToFile(json)
            }
        } catch (_: Exception) {
        }
        defaultHandler?.uncaughtException(thread, throwable)
    }

    private fun buildReport(thread: Thread, throwable: Throwable): Map<String, String> {
        val sw = StringWriter()
        throwable.printStackTrace(PrintWriter(sw))
        return mapOf(
            "stacktrace" to sw.toString(),
            "app_version" to BuildConfig.VERSION_NAME,
            "android_version" to Build.VERSION.RELEASE,
            "device" to "${Build.MANUFACTURER} ${Build.MODEL}",
            "thread" to thread.name,
            "timestamp" to System.currentTimeMillis().toString()
        )
    }

    private fun sendReport(json: String): Boolean {
        return try {
            val conn = URL(CRASH_ENDPOINT).openConnection() as HttpURLConnection
            conn.requestMethod = "POST"
            conn.setRequestProperty("Content-Type", "application/json")
            conn.connectTimeout = TIMEOUT_MS
            conn.readTimeout = TIMEOUT_MS
            conn.doOutput = true
            conn.outputStream.use { it.write(json.toByteArray()) }
            val code = conn.responseCode
            conn.disconnect()
            code in 200..299
        } catch (e: Exception) {
            false
        }
    }

    private fun saveToFile(json: String) {
        try {
            val dir = File(appContext.filesDir, CRASH_DIR)
            dir.mkdirs()
            val file = File(dir, "crash_${System.currentTimeMillis()}.json")
            file.writeText(json)
        } catch (_: Exception) {
        }
    }

    private fun retrySavedReports() {
        Thread {
            try {
                val dir = File(appContext.filesDir, CRASH_DIR)
                if (!dir.exists()) return@Thread
                dir.listFiles()?.forEach { file ->
                    try {
                        val json = file.readText()
                        if (sendReport(json)) {
                            file.delete()
                        }
                    } catch (_: Exception) {
                    }
                }
            } catch (e: Exception) {
                Log.e(AppConfig.TAG, "Failed to retry crash reports", e)
            }
        }.start()
    }
}
