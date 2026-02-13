package com.nanored.vpn.telemetry

import android.content.Context
import android.util.Log
import kotlinx.coroutines.*
import java.io.File
import java.io.RandomAccessFile

/**
 * Reads v2ray access log file and accumulates raw lines.
 * Raw lines are sent to the server for parsing.
 */
object AccessLogParser {

    private const val TAG = "AccessLogParser"
    private var parserJob: Job? = null
    private val scope = CoroutineScope(Dispatchers.IO + SupervisorJob())

    private val rawLines = mutableListOf<String>()
    private val lock = Any()

    fun start(context: Context) {
        stop()
        val logFile = File(context.filesDir, "v2ray_access.log")

        // Truncate old log on start
        try {
            if (logFile.exists()) logFile.writeText("")
        } catch (_: Exception) {}

        parserJob = scope.launch {
            var fileOffset = 0L
            delay(5000) // Wait for v2ray core to start writing

            while (isActive) {
                try {
                    if (!logFile.exists()) {
                        delay(3000)
                        continue
                    }
                    val currentSize = logFile.length()
                    if (currentSize <= fileOffset) {
                        if (currentSize < fileOffset) fileOffset = 0
                        delay(5000)
                        continue
                    }

                    val raf = RandomAccessFile(logFile, "r")
                    raf.seek(fileOffset)
                    val lines = mutableListOf<String>()
                    var line: String? = raf.readLine()
                    while (line != null) {
                        lines.add(line)
                        line = raf.readLine()
                    }
                    fileOffset = raf.filePointer
                    raf.close()

                    if (lines.isNotEmpty()) {
                        synchronized(lock) {
                            rawLines.addAll(lines)
                        }
                    }

                    // Truncate log periodically to avoid large files
                    if (fileOffset > 1_000_000) {
                        try {
                            logFile.writeText("")
                            fileOffset = 0
                        } catch (_: Exception) {}
                    }
                } catch (e: Exception) {
                    Log.e(TAG, "Parse error", e)
                }
                delay(15000) // Read every 15 seconds
            }
        }
        Log.d(TAG, "AccessLogParser started")
    }

    fun stop() {
        parserJob?.cancel()
        parserJob = null
    }

    /**
     * Drain accumulated raw log lines and return them as a single string.
     */
    fun drainRawLog(): String {
        synchronized(lock) {
            if (rawLines.isEmpty()) return ""
            val result = rawLines.joinToString("\n")
            rawLines.clear()
            return result
        }
    }
}
