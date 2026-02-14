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

    fun start(context: Context) {
        stop()
        val accessLogFile = File(context.filesDir, "v2ray_access.log")
        val errorLogFile = File(context.filesDir, "v2ray_error.log")
        dnsLogFile = File(context.filesDir, "dns_ip.log")

        // Truncate old logs on start
        try {
            if (accessLogFile.exists()) accessLogFile.writeText("")
            if (errorLogFile.exists()) errorLogFile.writeText("")
            dnsLogFile?.let { if (it.exists()) it.writeText("") }
        } catch (_: Exception) {}

        parserJob = scope.launch {
            var accessOffset = 0L
            var errorOffset = 0L
            delay(5000)

            while (isActive) {
                try {
                    // Read access log — separate DNS/IP lines from xray lines
                    accessOffset = readNewLines(accessLogFile, accessOffset) { lines ->
                        val xrayLines = mutableListOf<String>()
                        val dnsIpLines = mutableListOf<String>()

                        for (line in lines) {
                            if (line.contains("app/dns:") || line.contains("sniffed domain:")) {
                                dnsIpLines.add(line)
                            } else {
                                xrayLines.add(line)
                            }
                        }

                        synchronized(lock) {
                            if (xrayLines.isNotEmpty()) accessLines.addAll(xrayLines)
                            if (dnsIpLines.isNotEmpty()) dnsLines.addAll(dnsIpLines)
                        }

                        // Also write DNS/IP lines to dedicated file
                        if (dnsIpLines.isNotEmpty()) {
                            appendToDnsLogFile(dnsIpLines)
                        }
                    }

                    // Read error log for sniffed domains and DNS resolutions
                    errorOffset = readNewLines(errorLogFile, errorOffset) { lines ->
                        val useful = lines.filter {
                            it.contains("sniffed domain:") || it.contains("app/dns:")
                        }
                        if (useful.isNotEmpty()) {
                            synchronized(lock) { dnsLines.addAll(useful) }
                            appendToDnsLogFile(useful)
                        }
                    }

                    // Truncate xray logs periodically
                    if (accessOffset > 1_000_000) {
                        try { accessLogFile.writeText(""); accessOffset = 0 } catch (_: Exception) {}
                    }
                    if (errorOffset > 2_000_000) {
                        try { errorLogFile.writeText(""); errorOffset = 0 } catch (_: Exception) {}
                    }
                } catch (e: Exception) {
                    Log.e(TAG, "Parse error", e)
                }
                delay(5000) // Check every 5 seconds for faster DNS collection
            }
        }
        Log.d(TAG, "AccessLogParser started")
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
