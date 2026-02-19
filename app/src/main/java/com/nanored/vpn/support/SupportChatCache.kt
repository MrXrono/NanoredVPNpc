package com.nanored.vpn.support

import android.content.Context
import android.net.Uri
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.FileOutputStream
import java.net.URLConnection
import java.util.UUID

object SupportChatCache {
    private const val CACHE_DIR_NAME = "support_chat_cache"
    private const val MESSAGES_FILE_NAME = "messages.json"
    private const val MAX_CACHED_MESSAGES = 500

    private fun rootDir(context: Context): File = File(context.filesDir, CACHE_DIR_NAME).apply { mkdirs() }
    private fun mediaDir(context: Context): File = File(context.cacheDir, "support_media_cache").apply { mkdirs() }
    private fun messagesFile(context: Context): File = File(rootDir(context), MESSAGES_FILE_NAME)

    fun loadMessages(context: Context): List<SupportChatMessage> {
        val file = messagesFile(context)
        if (!file.exists()) return emptyList()
        return runCatching {
            val raw = file.readText(Charsets.UTF_8)
            val arr = JSONArray(raw)
            val out = ArrayList<SupportChatMessage>(arr.length())
            for (i in 0 until arr.length()) {
                val j = arr.optJSONObject(i) ?: continue
                out.add(
                    SupportChatMessage(
                        id = j.optString("id"),
                        direction = runCatching { SupportDirection.valueOf(j.optString("direction")) }
                            .getOrDefault(SupportDirection.SUPPORT_TO_APP),
                        messageType = runCatching { SupportMessageType.valueOf(j.optString("messageType")) }
                            .getOrDefault(SupportMessageType.TEXT),
                        text = j.optString("text").takeIf { it.isNotBlank() },
                        fileName = j.optString("fileName").takeIf { it.isNotBlank() },
                        mimeType = j.optString("mimeType").takeIf { it.isNotBlank() },
                        hasAttachment = j.optBoolean("hasAttachment", false),
                        createdAtRaw = j.optString("createdAtRaw"),
                        localUri = j.optString("localUri").takeIf { it.isNotBlank() },
                        isPending = j.optBoolean("isPending", false),
                    )
                )
            }
            out
        }.getOrDefault(emptyList())
    }

    fun saveMessages(context: Context, messages: List<SupportChatMessage>) {
        val trimmed = if (messages.size > MAX_CACHED_MESSAGES) {
            messages.takeLast(MAX_CACHED_MESSAGES)
        } else messages
        val arr = JSONArray()
        for (m in trimmed) {
            arr.put(
                JSONObject().apply {
                    put("id", m.id)
                    put("direction", m.direction.name)
                    put("messageType", m.messageType.name)
                    put("text", m.text ?: "")
                    put("fileName", m.fileName ?: "")
                    put("mimeType", m.mimeType ?: "")
                    put("hasAttachment", m.hasAttachment)
                    put("createdAtRaw", m.createdAtRaw)
                    put("localUri", m.localUri ?: "")
                    put("isPending", m.isPending)
                }
            )
        }
        runCatching { messagesFile(context).writeText(arr.toString(), Charsets.UTF_8) }
    }

    fun findCachedLocalUri(context: Context, messageId: String): String? {
        val f = findMediaFile(context, messageId) ?: return null
        return Uri.fromFile(f).toString()
    }

    fun findMediaFile(context: Context, messageId: String): File? {
        val prefix = "${sanitize(messageId)}__"
        return mediaDir(context).listFiles()?.firstOrNull { it.name.startsWith(prefix) && it.length() > 0L }
    }

    fun cacheFromUri(
        context: Context,
        messageId: String,
        sourceUri: Uri,
        preferredName: String?,
        mimeType: String?,
    ): String? {
        val name = resolveName(preferredName, mimeType)
        val target = mediaFile(context, messageId, name)
        return runCatching {
            context.contentResolver.openInputStream(sourceUri)?.use { input ->
                FileOutputStream(target, false).use { out -> input.copyTo(out) }
            } ?: return null
            Uri.fromFile(target).toString()
        }.getOrNull()
    }

    fun cacheFromBytes(
        context: Context,
        messageId: String,
        preferredName: String?,
        mimeType: String?,
        bytes: ByteArray,
    ): File {
        val name = resolveName(preferredName, mimeType)
        val target = mediaFile(context, messageId, name)
        FileOutputStream(target, false).use { it.write(bytes) }
        return target
    }

    fun cacheFromFile(
        context: Context,
        messageId: String,
        sourceFile: File,
        preferredName: String?,
        mimeType: String?,
    ): File {
        val name = resolveName(preferredName ?: sourceFile.name, mimeType)
        val target = mediaFile(context, messageId, name)
        sourceFile.inputStream().use { input ->
            FileOutputStream(target, false).use { out -> input.copyTo(out) }
        }
        return target
    }

    private fun mediaFile(context: Context, messageId: String, name: String): File {
        val safeId = sanitize(messageId)
        val safeName = sanitize(name)
        return File(mediaDir(context), "${safeId}__${safeName}")
    }

    private fun resolveName(preferredName: String?, mimeType: String?): String {
        val base = preferredName?.trim().takeIf { !it.isNullOrBlank() } ?: "attachment"
        if (base.contains('.')) return base
        val extFromMime = mimeType?.let { URLConnection.guessContentTypeFromName("f.$it") }
        val ext = when {
            mimeType.isNullOrBlank() -> null
            mimeType.contains("jpeg", ignoreCase = true) -> "jpg"
            mimeType.contains("png", ignoreCase = true) -> "png"
            mimeType.contains("webp", ignoreCase = true) -> "webp"
            mimeType.contains("gif", ignoreCase = true) -> "gif"
            mimeType.contains("mp4", ignoreCase = true) -> "mp4"
            mimeType.contains("mpeg", ignoreCase = true) -> "mp3"
            mimeType.contains("ogg", ignoreCase = true) -> "ogg"
            mimeType.contains("pdf", ignoreCase = true) -> "pdf"
            mimeType.contains("text/plain", ignoreCase = true) -> "txt"
            else -> null
        }
        return if (!ext.isNullOrBlank()) "$base.$ext" else base
    }

    private fun sanitize(value: String): String {
        return value.replace("\\", "_")
            .replace("/", "_")
            .replace(":", "_")
            .replace("\n", " ")
            .replace("\r", " ")
            .trim()
            .ifBlank { UUID.randomUUID().toString() }
    }
}
