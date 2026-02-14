package com.nanored.vpn.telemetry

import android.content.Context
import android.util.Log
import kotlinx.coroutines.*
import java.io.File
import java.io.RandomAccessFile

/**
 * Reads v2ray access log and error log (DNS queries).
 * Separates DNS/IP entries into a dedicated log file (dns_ip.log).
 * Raw xray access log lines are accumulated separately.
 */
object AccessLogParser {

    private const val TAG = "AccessLogParser"
    private var parserJob: Job? = null
    private val scope = CoroutineScope(Dispatchers.IO + SupervisorJob())

    // Xray access log lines (without DNS)
    private val accessLines = mutableListOf<String>()
    // DNS + IP lines (sniffed domains, DNS resolutions, raw IPs without DNS)
    private val dnsLines = mutableListOf<String>()
    private val lock = Any()

    private var dnsLogFile: File? = null
    private var accessLogFile: File? = null
    private var errorLogFile: File? = null
    @Volatile private var accessOffset = 0L
    @Volatile private var errorOffset = 0L

    fun start(context: Context) {
        stop()
        accessLogFile = File(context.filesDir, "v2ray_access.log")
        errorLogFile = File(context.filesDir, "v2ray_error.log")
        dnsLogFile = File(context.filesDir, "dns_ip.log")
        accessOffset = 0L
        errorOffset = 0L

        // Truncate old logs on start
        try {
            accessLogFile?.let { if (it.exists()) it.writeText("") }
            errorLogFile?.let { if (it.exists()) it.writeText("") }
            dnsLogFile?.let { if (it.exists()) it.writeText("") }
        } catch (_: Exception) {}

        parserJob = scope.launch {
            delay(5000)

            while (isActive) {
                try {
                    readAccessLog()
                    readErrorLog()

                    // Truncate xray logs periodically
                    if (accessOffset > 1_000_000) {
                        try { accessLogFile?.writeText(""); accessOffset = 0 } catch (_: Exception) {}
                    }
                    if (errorOffset > 2_000_000) {
                        try { errorLogFile?.writeText(""); errorOffset = 0 } catch (_: Exception) {}
                    }
                } catch (e: Exception) {
                    Log.e(TAG, "Parse error", e)
                }
                delay(5000)
            }
        }
        Log.d(TAG, "AccessLogParser started")
    }

    private fun readAccessLog() {
        val file = accessLogFile ?: return
        accessOffset = readNewLines(file, accessOffset) { lines ->
            val xrayLines = mutableListOf<String>()
            val dnsIpLines = mutableListOf<String>()

            for (line in lines) {
                if (line.contains("app/dns:") || line.contains("sniffed domain:")
                    || line.contains("default route for")) {
                    dnsIpLines.add(line)
                } else {
                    xrayLines.add(line)
                }
            }

            synchronized(lock) {
                if (xrayLines.isNotEmpty()) accessLines.addAll(xrayLines)
                if (dnsIpLines.isNotEmpty()) dnsLines.addAll(dnsIpLines)
            }

            if (dnsIpLines.isNotEmpty()) {
                appendToDnsLogFile(dnsIpLines)
            }
        }
    }

    private fun readErrorLog() {
        val file = errorLogFile ?: return
        errorOffset = readNewLines(file, errorOffset) { lines ->
            val useful = lines.filter {
                it.contains("sniffed domain:") || it.contains("app/dns:")
                    || it.contains("default route for") || it.contains("tunneling request to")
            }
            if (useful.isNotEmpty()) {
                synchronized(lock) { dnsLines.addAll(useful) }
                appendToDnsLogFile(useful)
            }
        }
    }

    /**
     * Final synchronous read of remaining data from both log files.
     * Must be called BEFORE stop() and BEFORE endSession() to ensure no data is lost.
     */
    fun finalRead() {
        try {
            readAccessLog()
            readErrorLog()
            Log.d(TAG, "Final read completed: ${accessLines.size} access, ${dnsLines.size} dns lines")
        } catch (e: Exception) {
            Log.e(TAG, "Final read error", e)
        }
    }

    private fun appendToDnsLogFile(lines: List<String>) {
        try {
            dnsLogFile?.appendText(lines.joinToString("\n") + "\n")
        } catch (_: Exception) {}
    }

    private fun readNewLines(file: File, offset: Long, onLines: (List<String>) -> Unit): Long {
        if (!file.exists()) return offset
        val currentSize = file.length()
        if (currentSize <= offset) {
            return if (currentSize < offset) 0 else offset
        }
        val raf = RandomAccessFile(file, "r")
        raf.seek(offset)
        val lines = mutableListOf<String>()
        var line: String? = raf.readLine()
        while (line != null) {
            lines.add(line)
            line = raf.readLine()
        }
        val newOffset = raf.filePointer
        raf.close()
        if (lines.isNotEmpty()) onLines(lines)
        return newOffset
    }

    fun stop() {
        parserJob?.cancel()
        parserJob = null
    }

    /**
     * Drain accumulated xray access log lines (without DNS).
     */
    fun drainRawLog(): String {
        synchronized(lock) {
            if (accessLines.isEmpty()) return ""
            val result = accessLines.joinToString("\n")
            accessLines.clear()
            return result
        }
    }

    /**
     * Drain accumulated DNS/IP log lines and clear the dns_ip.log file.
     */
    fun drainDnsLog(): String {
        synchronized(lock) {
            if (dnsLines.isEmpty()) return ""
            val result = dnsLines.joinToString("\n")
            dnsLines.clear()
            // Clear the dns_ip.log file after draining
            try { dnsLogFile?.writeText("") } catch (_: Exception) {}
            return result
        }
    }
}
