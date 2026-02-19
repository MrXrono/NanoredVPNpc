package com.nanored.vpn.support

import android.content.Context
import android.net.Uri
import android.util.Log
import com.nanored.vpn.telemetry.NanoredTelemetry
import org.json.JSONArray
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.io.DataOutputStream
import java.io.File
import java.io.OutputStream
import java.io.FileInputStream
import java.net.HttpURLConnection
import java.net.URL
import java.util.UUID

object SupportChatApi {
    private const val TAG = "SupportChatApi"
    private const val PREFS_NAME = "nanored_telemetry"
    private const val KEY_API_KEY = "api_key"
    private const val BASE_URL = "https://api.nanored.top"

    private fun apiKey(context: Context): String? =
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE).getString(KEY_API_KEY, null)

    fun fetchMessages(context: Context, afterId: String? = null, limit: Int = 120): SupportMessagesPage {
        val key = apiKey(context) ?: return SupportMessagesPage(emptyList(), 0)
        val url = buildString {
            append("$BASE_URL/api/v1/client/support/messages?limit=$limit")
            if (!afterId.isNullOrBlank()) append("&after_id=$afterId")
        }
        val resp = requestJson(context, "GET", url, key, null, "application/json")
        if (resp == null) return SupportMessagesPage(emptyList(), 0)

        val json = JSONObject(resp)
        val arr = json.optJSONArray("items") ?: JSONArray()
        val items = ArrayList<SupportChatMessage>(arr.length())
        for (i in 0 until arr.length()) {
            val it = arr.optJSONObject(i) ?: continue
            fun optStringOrNull(key: String): String? {
                val raw = it.optString(key, "")
                val v = raw.trim()
                if (v.isEmpty()) return null
                if (v.equals("null", ignoreCase = true)) return null
                return v
            }
            items.add(
                SupportChatMessage(
                    id = it.optString("id"),
                    direction = runCatching { SupportDirection.valueOf(it.optString("direction").uppercase()) }
                        .getOrDefault(SupportDirection.SUPPORT_TO_APP),
                    messageType = runCatching { SupportMessageType.valueOf(it.optString("message_type").uppercase()) }
                        .getOrDefault(SupportMessageType.TEXT),
                    text = optStringOrNull("text"),
                    fileName = optStringOrNull("file_name"),
                    mimeType = optStringOrNull("mime_type"),
                    hasAttachment = it.optBoolean("has_attachment", false),
                    createdAtRaw = it.optString("created_at"),
                )
            )
        }
        return SupportMessagesPage(items = items, unreadCount = json.optInt("unread_count", 0))
    }

    fun unreadCount(context: Context): Int {
        val key = apiKey(context) ?: return 0
        val resp = requestJson(context, "GET", "$BASE_URL/api/v1/client/support/unread", key, null, "application/json") ?: return 0
        return runCatching { JSONObject(resp).optInt("unread_count", 0) }.getOrDefault(0)
    }

    fun markRead(context: Context, uptoId: String? = null) {
        val key = apiKey(context) ?: return
        val body = JSONObject().apply {
            if (!uptoId.isNullOrBlank()) put("upto_message_id", uptoId)
        }.toString()
        requestJson(context, "POST", "$BASE_URL/api/v1/client/support/read", key, body, "application/json")
    }

    fun sendText(context: Context, text: String): SupportChatMessage? {
        val key = apiKey(context) ?: return null
        val boundary = "----NanoredBoundary${UUID.randomUUID()}"
        val baos = ByteArrayOutputStream()
        val out = DataOutputStream(baos)
        writeFormField(out, boundary, "text", text)
        out.writeBytes("--$boundary--\r\n")
        out.flush()

        val resp = requestBytes(
            context = context,
            method = "POST",
            rawUrl = "$BASE_URL/api/v1/client/support/send",
            apiKey = key,
            bodyBytes = baos.toByteArray(),
            contentType = "multipart/form-data; boundary=$boundary",
        ) ?: return null
        return parseMessage(JSONObject(resp))
    }

    fun sendLogs(context: Context, logs: String): SupportChatMessage? {
        // Send logs as a file attachment (Telegram-friendly and no text-size limit issues).
        val fileName = "nanored_logs_${System.currentTimeMillis()}.txt"
        val file = File(context.cacheDir, fileName)
        runCatching { file.writeText(logs.replace("\u0000", ""), Charsets.UTF_8) }
        return sendFile(context, Uri.fromFile(file), overrideName = fileName, overrideMime = "text/plain")
    }

    fun sendFile(
        context: Context,
        fileUri: Uri,
        overrideName: String? = null,
        overrideMime: String? = null,
        attempt: Int = 0,
    ): SupportChatMessage? {
        val key = apiKey(context) ?: return null
        val resolver = context.contentResolver
        val name = (overrideName ?: queryFileName(context, fileUri))?.takeIf { it.isNotBlank() } ?: "attachment.bin"
        val mimeType = (overrideMime ?: resolver.getType(fileUri))?.takeIf { it.isNotBlank() } ?: "application/octet-stream"

        // Pre-check size when possible to give immediate feedback and avoid OOM.
        val size = queryFileSize(context, fileUri)
        if (size != null && size > 50L * 1024L * 1024L) {
            return null
        }

        val boundary = "----NanoredBoundary${UUID.randomUUID()}"
        val url = "$BASE_URL/api/v1/client/support/send"
        var conn: HttpURLConnection? = null
        return try {
            conn = (URL(url).openConnection() as HttpURLConnection).apply {
                requestMethod = "POST"
                connectTimeout = 20_000
                readTimeout = 90_000
                doInput = true
                doOutput = true
                setChunkedStreamingMode(0)
                setRequestProperty("Accept", "application/json")
                setRequestProperty("X-API-Key", key)
                setRequestProperty("Content-Type", "multipart/form-data; boundary=$boundary")
            }

            conn.outputStream.use { os ->
                val out = DataOutputStream(os)
                writeFileFieldHeader(out, boundary, "file", name, mimeType)
                openInputStream(resolver, fileUri)?.use { input ->
                    input.copyTo(os, bufferSize = 64 * 1024)
                } ?: return null
                out.writeBytes("\r\n")
                out.writeBytes("--$boundary--\r\n")
                out.flush()
            }

            val code = conn.responseCode
            val respBytes = if (code in 200..299) conn.inputStream.readBytes() else conn.errorStream?.readBytes()
            if (respBytes == null) return null
            val resp = respBytes.toString(Charsets.UTF_8)
            if (code !in 200..299) {
                Log.w(TAG, "sendFile error $code: $resp")
                if (code == 401 && attempt < 2) {
                    if (NanoredTelemetry.forceRegisterBlocking()) {
                        return sendFile(context, fileUri, overrideName, overrideMime, attempt + 1)
                    }
                }
                if (code in 500..599 && attempt < 2) {
                    Thread.sleep(500L * (attempt + 1))
                    return sendFile(context, fileUri, overrideName, overrideMime, attempt + 1)
                }
                return null
            }
            parseMessage(JSONObject(resp))
        } catch (e: Exception) {
            Log.e(TAG, "sendFile failed (attempt=$attempt)", e)
            if (attempt < 1) {
                return sendFile(context, fileUri, overrideName, overrideMime, attempt + 1)
            }
            null
        } finally {
            conn?.disconnect()
        }
    }

    fun downloadAttachment(context: Context, messageId: String, fallbackName: String?): DownloadedAttachment? {
        val key = apiKey(context) ?: return null
        var conn: HttpURLConnection? = null
        return try {
            conn = URL("$BASE_URL/api/v1/client/support/media/$messageId").openConnection() as HttpURLConnection
            conn.requestMethod = "GET"
            conn.connectTimeout = 15_000
            conn.readTimeout = 25_000
            conn.setRequestProperty("X-API-Key", key)
            conn.doInput = true

            if (conn.responseCode !in 200..299) {
                return null
            }

            val mime = conn.contentType ?: "application/octet-stream"
            val outputName = fallbackName?.ifBlank { null } ?: "support-$messageId.bin"
            val output = File(context.cacheDir, outputName)
            conn.inputStream.use { input ->
                output.outputStream().use { out -> input.copyTo(out) }
            }
            DownloadedAttachment(file = output, mimeType = mime)
        } catch (e: Exception) {
            Log.e(TAG, "Attachment download failed", e)
            null
        } finally {
            conn?.disconnect()
        }
    }

    fun downloadAttachmentBytes(context: Context, messageId: String): ByteArray? {
        val key = apiKey(context) ?: return null
        var conn: HttpURLConnection? = null
        return try {
            conn = URL("$BASE_URL/api/v1/client/support/media/$messageId").openConnection() as HttpURLConnection
            conn.requestMethod = "GET"
            conn.connectTimeout = 15_000
            conn.readTimeout = 25_000
            conn.setRequestProperty("X-API-Key", key)
            conn.doInput = true

            if (conn.responseCode !in 200..299) return null
            conn.inputStream.use { it.readBytes() }
        } catch (_: Exception) {
            null
        } finally {
            conn?.disconnect()
        }
    }

    private fun writeFormField(out: DataOutputStream, boundary: String, name: String, value: String) {
        out.writeBytes("--$boundary\r\n")
        out.writeBytes("Content-Disposition: form-data; name=\"$name\"\r\n")
        out.writeBytes("Content-Type: text/plain; charset=UTF-8\r\n\r\n")
        out.write(value.toByteArray(Charsets.UTF_8))
        out.writeBytes("\r\n")
    }

    private fun writeFileFieldHeader(
        out: DataOutputStream,
        boundary: String,
        name: String,
        fileName: String,
        mimeType: String,
    ) {
        out.writeBytes("--$boundary\r\n")
        out.writeBytes("Content-Disposition: form-data; name=\"$name\"; filename=\"$fileName\"\r\n")
        out.writeBytes("Content-Type: $mimeType\r\n\r\n")
    }

    private fun queryFileName(context: Context, uri: Uri): String? {
        context.contentResolver.query(uri, null, null, null, null)?.use { cursor ->
            val idx = cursor.getColumnIndex(android.provider.OpenableColumns.DISPLAY_NAME)
            if (idx >= 0 && cursor.moveToFirst()) return cursor.getString(idx)
        }
        return null
    }

    private fun queryFileSize(context: Context, uri: Uri): Long? {
        context.contentResolver.query(uri, null, null, null, null)?.use { cursor ->
            val idx = cursor.getColumnIndex(android.provider.OpenableColumns.SIZE)
            if (idx >= 0 && cursor.moveToFirst()) return cursor.getLong(idx)
        }
        return null
    }

    private fun openInputStream(resolver: android.content.ContentResolver, uri: Uri): java.io.InputStream? {
        return if (uri.scheme.equals("file", ignoreCase = true)) {
            val path = uri.path ?: return null
            FileInputStream(File(path))
        } else {
            resolver.openInputStream(uri)
        }
    }

    private fun parseMessage(json: JSONObject): SupportChatMessage {
        fun jsonStringOrNull(key: String): String? {
            val raw = json.optString(key, "")
            val v = raw.trim()
            if (v.isEmpty()) return null
            // org.json returns literal "null" for JSONObject.NULL; normalize it.
            if (v.equals("null", ignoreCase = true)) return null
            return v
        }
        return SupportChatMessage(
            id = json.optString("id"),
            direction = runCatching { SupportDirection.valueOf(json.optString("direction").uppercase()) }
                .getOrDefault(SupportDirection.APP_TO_SUPPORT),
            messageType = runCatching { SupportMessageType.valueOf(json.optString("message_type").uppercase()) }
                .getOrDefault(SupportMessageType.TEXT),
            text = jsonStringOrNull("text"),
            fileName = jsonStringOrNull("file_name"),
            mimeType = jsonStringOrNull("mime_type"),
            // Some server-side messages may miss has_attachment for outgoing app->support attachments.
            hasAttachment = json.optBoolean("has_attachment", false) ||
                jsonStringOrNull("file_name") != null ||
                jsonStringOrNull("mime_type") != null ||
                runCatching { SupportMessageType.valueOf(json.optString("message_type").uppercase()) }
                    .getOrDefault(SupportMessageType.TEXT) != SupportMessageType.TEXT,
            createdAtRaw = json.optString("created_at"),
        )
    }

    private fun requestJson(
        context: Context,
        method: String,
        rawUrl: String,
        apiKey: String,
        body: String?,
        contentType: String,
    ): String? {
        val bytes = body?.toByteArray(Charsets.UTF_8)
        return requestBytes(context, method, rawUrl, apiKey, bytes, contentType)
    }

    private fun requestBytes(
        context: Context,
        method: String,
        rawUrl: String,
        apiKey: String,
        bodyBytes: ByteArray?,
        contentType: String,
    ): String? {
        return requestBytesInternal(context, method, rawUrl, apiKey, bodyBytes, contentType, retriedAfter401 = false)
    }

    private fun requestBytesInternal(
        context: Context,
        method: String,
        rawUrl: String,
        apiKey: String,
        bodyBytes: ByteArray?,
        contentType: String,
        retriedAfter401: Boolean,
    ): String? {
        var conn: HttpURLConnection? = null
        return try {
            conn = URL(rawUrl).openConnection() as HttpURLConnection
            conn.requestMethod = method
            conn.connectTimeout = 15_000
            conn.readTimeout = 25_000
            conn.setRequestProperty("Accept", "application/json")
            conn.setRequestProperty("X-API-Key", apiKey)
            conn.setRequestProperty("Content-Type", contentType)
            conn.doInput = true

            if (bodyBytes != null) {
                conn.doOutput = true
                conn.outputStream.use { it.write(bodyBytes) }
            }

            val code = conn.responseCode
            val stream = if (code in 200..299) conn.inputStream else conn.errorStream
            val response = stream?.bufferedReader(Charsets.UTF_8)?.use { it.readText() }
            if (code !in 200..299) {
                Log.w(TAG, "Support API error $code: $response")
                if (code == 401 && !retriedAfter401) {
                    // API key can become invalid if multiple registrations race and rotate it.
                    // Force a fresh register and retry once with the latest key from prefs.
                    runCatching { NanoredTelemetry.forceRegisterBlocking() }
                    val newKey = apiKey(context)
                    if (!newKey.isNullOrBlank()) {
                        return requestBytesInternal(context, method, rawUrl, newKey, bodyBytes, contentType, retriedAfter401 = true)
                    }
                }
                return null
            }
            response
        } catch (e: Exception) {
            Log.e(TAG, "Support API request failed", e)
            null
        } finally {
            conn?.disconnect()
        }
    }
}
