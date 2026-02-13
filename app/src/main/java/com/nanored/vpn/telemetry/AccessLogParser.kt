package com.nanored.vpn.telemetry

import android.content.Context
import android.util.Log
import kotlinx.coroutines.*
import java.io.File
import java.io.RandomAccessFile

/**
 * Reads v2ray access log and error log (DNS queries).
 * Raw lines are sent to the server for parsing.
 */
object AccessLogParser {

    private const val TAG = "AccessLogParser"
    private var parserJob: Job? = null
    private val scope = CoroutineScope(Dispatchers.IO + SupervisorJob())

    private val accessLines = mutableListOf<String>()
    private val dnsLines = mutableListOf<String>()
    private val lock = Any()

    fun start(context: Context) {
        stop()
        val accessLogFile = File(context.filesDir, "v2ray_access.log")
        val errorLogFile = File(context.filesDir, "v2ray_error.log")

        // Truncate old logs on start
        try {
            if (accessLogFile.exists()) accessLogFile.writeText("")
            if (errorLogFile.exists()) errorLogFile.writeText("")
        } catch (_: Exception) {}

        parserJob = scope.launch {
            var accessOffset = 0L
            var errorOffset = 0L
            delay(5000)

            while (isActive) {
                try {
                    // Read access log
                    accessOffset = readNewLines(accessLogFile, accessOffset) { lines ->
                        synchronized(lock) { accessLines.addAll(lines) }
                    }

                    // Read error log for sniffed domains and DNS resolutions
                    errorOffset = readNewLines(errorLogFile, errorOffset) { lines ->
                        val useful = lines.filter {
                            it.contains("sniffed domain:") || it.contains("app/dns:")
                        }
                        if (useful.isNotEmpty()) {
                            synchronized(lock) { dnsLines.addAll(useful) }
                        }
                    }

                    // Truncate logs periodically
                    if (accessOffset > 1_000_000) {
                        try { accessLogFile.writeText(""); accessOffset = 0 } catch (_: Exception) {}
                    }
                    if (errorOffset > 2_000_000) {
                        try { errorLogFile.writeText(""); errorOffset = 0 } catch (_: Exception) {}
                    }
                } catch (e: Exception) {
                    Log.e(TAG, "Parse error", e)
                }
                delay(15000)
            }
        }
        Log.d(TAG, "AccessLogParser started")
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
     * Drain accumulated access log lines.
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
     * Drain accumulated DNS log lines.
     */
    fun drainDnsLog(): String {
        synchronized(lock) {
            if (dnsLines.isEmpty()) return ""
            val result = dnsLines.joinToString("\n")
            dnsLines.clear()
            return result
        }
    }
}
