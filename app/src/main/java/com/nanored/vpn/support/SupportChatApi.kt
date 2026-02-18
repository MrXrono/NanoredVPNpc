package com.nanored.vpn.support

import android.content.Context
import android.net.Uri
import android.util.Log
import org.json.JSONArray
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.io.DataOutputStream
import java.io.File
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
        val resp = request("GET", url, key, null, "application/json")
        if (resp == null) return SupportMessagesPage(emptyList(), 0)

        val json = JSONObject(resp)
        val arr = json.optJSONArray("items") ?: JSONArray()
        val items = ArrayList<SupportChatMessage>(arr.length())
        for (i in 0 until arr.length()) {
            val it = arr.optJSONObject(i) ?: continue
            items.add(
                SupportChatMessage(
                    id = it.optString("id"),
                    direction = runCatching { SupportDirection.valueOf(it.optString("direction").uppercase()) }
                        .getOrDefault(SupportDirection.SUPPORT_TO_APP),
                    messageType = runCatching { SupportMessageType.valueOf(it.optString("message_type").uppercase()) }
                        .getOrDefault(SupportMessageType.TEXT),
                    text = it.optString("text").ifBlank { null },
                    fileName = it.optString("file_name").ifBlank { null },
                    mimeType = it.optString("mime_type").ifBlank { null },
                    hasAttachment = it.optBoolean("has_attachment", false),
                    createdAtRaw = it.optString("created_at"),
                )
            )
        }
        return SupportMessagesPage(items = items, unreadCount = json.optInt("unread_count", 0))
    }

    fun unreadCount(context: Context): Int {
        val key = apiKey(context) ?: return 0
        val resp = request("GET", "$BASE_URL/api/v1/client/support/unread", key, null, "application/json") ?: return 0
        return runCatching { JSONObject(resp).optInt("unread_count", 0) }.getOrDefault(0)
    }

    fun markRead(context: Context, uptoId: String? = null) {
        val key = apiKey(context) ?: return
        val body = JSONObject().apply {
            if (!uptoId.isNullOrBlank()) put("upto_message_id", uptoId)
        }.toString()
        request("POST", "$BASE_URL/api/v1/client/support/read", key, body, "application/json")
    }

    fun sendText(context: Context, text: String): SupportChatMessage? {
        val key = apiKey(context) ?: return null
        val boundary = "----NanoredBoundary${UUID.randomUUID()}"
        val baos = ByteArrayOutputStream()
        val out = DataOutputStream(baos)
        writeFormField(out, boundary, "text", text)
        out.writeBytes("--$boundary--\r\n")
        out.flush()

        val resp = request(
            method = "POST",
            rawUrl = "$BASE_URL/api/v1/client/support/send",
            apiKey = key,
            bodyBytes = baos.toByteArray(),
            contentType = "multipart/form-data; boundary=$boundary",
        ) ?: return null
        return parseMessage(JSONObject(resp))
    }

    fun sendLogs(context: Context, logs: String): SupportChatMessage? {
        return sendText(context, "[Logs]\n$logs")
    }

    fun sendFile(context: Context, fileUri: Uri): SupportChatMessage? {
        val key = apiKey(context) ?: return null
        val resolver = context.contentResolver
        val name = queryFileName(context, fileUri) ?: "attachment.bin"
        val mimeType = resolver.getType(fileUri) ?: "application/octet-stream"
        val content = resolver.openInputStream(fileUri)?.use { it.readBytes() } ?: return null

        val boundary = "----NanoredBoundary${UUID.randomUUID()}"
        val baos = ByteArrayOutputStream()
        val out = DataOutputStream(baos)
        writeFileField(out, boundary, "file", name, mimeType, content)
        out.writeBytes("--$boundary--\r\n")
        out.flush()

        val resp = request(
            method = "POST",
            rawUrl = "$BASE_URL/api/v1/client/support/send",
            apiKey = key,
            bodyBytes = baos.toByteArray(),
            contentType = "multipart/form-data; boundary=$boundary",
        ) ?: return null
        return parseMessage(JSONObject(resp))
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

    private fun writeFormField(out: DataOutputStream, boundary: String, name: String, value: String) {
        out.writeBytes("--$boundary\r\n")
        out.writeBytes("Content-Disposition: form-data; name=\"$name\"\r\n\r\n")
        out.writeBytes(value)
        out.writeBytes("\r\n")
    }

    private fun writeFileField(
        out: DataOutputStream,
        boundary: String,
        name: String,
        fileName: String,
        mimeType: String,
        content: ByteArray,
    ) {
        out.writeBytes("--$boundary\r\n")
        out.writeBytes("Content-Disposition: form-data; name=\"$name\"; filename=\"$fileName\"\r\n")
        out.writeBytes("Content-Type: $mimeType\r\n\r\n")
        out.write(content)
        out.writeBytes("\r\n")
    }

    private fun queryFileName(context: Context, uri: Uri): String? {
        context.contentResolver.query(uri, null, null, null, null)?.use { cursor ->
            val idx = cursor.getColumnIndex(android.provider.OpenableColumns.DISPLAY_NAME)
            if (idx >= 0 && cursor.moveToFirst()) return cursor.getString(idx)
        }
        return null
    }

    private fun parseMessage(json: JSONObject): SupportChatMessage {
        return SupportChatMessage(
            id = json.optString("id"),
            direction = runCatching { SupportDirection.valueOf(json.optString("direction").uppercase()) }
                .getOrDefault(SupportDirection.APP_TO_SUPPORT),
            messageType = runCatching { SupportMessageType.valueOf(json.optString("message_type").uppercase()) }
                .getOrDefault(SupportMessageType.TEXT),
            text = json.optString("text").ifBlank { null },
            fileName = json.optString("file_name").ifBlank { null },
            mimeType = json.optString("mime_type").ifBlank { null },
            hasAttachment = json.optBoolean("has_attachment", false),
            createdAtRaw = json.optString("created_at"),
        )
    }

    private fun request(
        method: String,
        rawUrl: String,
        apiKey: String,
        body: String?,
        contentType: String,
    ): String? {
        val bytes = body?.toByteArray(Charsets.UTF_8)
        return request(method, rawUrl, apiKey, bytes, contentType)
    }

    private fun request(
        method: String,
        rawUrl: String,
        apiKey: String,
        bodyBytes: ByteArray?,
        contentType: String,
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
            val response = stream?.bufferedReader()?.use { it.readText() }
            if (code !in 200..299) {
                Log.w(TAG, "Support API error $code: $response")
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
