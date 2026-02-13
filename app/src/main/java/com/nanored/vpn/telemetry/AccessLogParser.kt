package com.nanored.vpn.telemetry

import android.content.Context
import android.util.Log
import kotlinx.coroutines.*
import java.io.File
import java.io.RandomAccessFile

/**
 * Parses v2ray access log file to extract SNI domains and connection info.
 * Runs as a background coroutine, tailing the log file.
 *
 * V2Ray access log format:
 * 2026/01/15 12:34:56 tcp:1.2.3.4:443 accepted proxy >> domain.com:443
 * 2026/01/15 12:34:56 from 10.1.10.1:12345 accepted tcp:domain.com:443 [proxy]
 */
object AccessLogParser {

    private const val TAG = "AccessLogParser"
    private var parserJob: Job? = null
    private val scope = CoroutineScope(Dispatchers.IO + SupervisorJob())

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
                        // File was truncated or no new data
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

                    for (l in lines) {
                        parseLine(l)
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
                delay(15000) // Parse every 15 seconds
            }
        }
        Log.d(TAG, "AccessLogParser started")
    }

    fun stop() {
        parserJob?.cancel()
        parserJob = null
    }

    private fun parseLine(line: String) {
        try {
            // Extract domain from access log
            // Format variants:
            // "... accepted tcp:domain.com:443 [proxy]"
            // "... >> domain.com:443"
            // "... tcp:1.2.3.4:443 accepted proxy >> domain.com:443 email: ..."

            val domain: String?
            val destIp: String?
            val destPort: Int?

            if (line.contains(">>")) {
                // Extract destination after >>
                val parts = line.substringAfter(">>").trim().split(" ")[0]
                val hostPort = parseHostPort(parts)
                domain = hostPort.first
                destPort = hostPort.second

                // Check if domain is actually an IP
                destIp = if (isIpAddress(domain ?: "")) domain else null
                val sniDomain = if (!isIpAddress(domain ?: "")) domain else null

                if (sniDomain != null) {
                    NanoredTelemetry.addSNI(sniDomain)
                }
                if (destIp != null && destPort != null) {
                    NanoredTelemetry.addConnection(destIp, destPort, "TCP", sniDomain)
                }
            } else if (line.contains("accepted")) {
                // "... accepted tcp:domain.com:443 [proxy]"
                val accepted = line.substringAfter("accepted").trim()
                val target = accepted.split(" ")[0]
                    .removePrefix("tcp:")
                    .removePrefix("udp:")
                val hostPort = parseHostPort(target)
                domain = hostPort.first
                destPort = hostPort.second

                destIp = if (isIpAddress(domain ?: "")) domain else null
                val sniDomain = if (!isIpAddress(domain ?: "")) domain else null

                if (sniDomain != null) {
                    NanoredTelemetry.addSNI(sniDomain)
                }
            }
        } catch (_: Exception) {
            // Skip malformed lines
        }
    }

    private fun parseHostPort(s: String): Pair<String?, Int?> {
        val stripped = s.trim()
        if (stripped.isEmpty()) return null to null

        // Handle [ipv6]:port
        if (stripped.startsWith("[")) {
            val closeBracket = stripped.indexOf(']')
            if (closeBracket < 0) return stripped to null
            val host = stripped.substring(1, closeBracket)
            val port = if (closeBracket + 1 < stripped.length && stripped[closeBracket + 1] == ':') {
                stripped.substring(closeBracket + 2).toIntOrNull()
            } else null
            return host to port
        }

        // host:port
        val lastColon = stripped.lastIndexOf(':')
        if (lastColon < 0) return stripped to null
        val host = stripped.substring(0, lastColon)
        val port = stripped.substring(lastColon + 1).toIntOrNull()
        return host to port
    }

    private fun isIpAddress(s: String): Boolean {
        // Quick check for IPv4 or IPv6
        return s.matches(Regex("^\\d{1,3}(\\.\\d{1,3}){3}$")) || s.contains(":")
    }
}
