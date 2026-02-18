package com.nanored.vpn.support

import java.time.Instant
import java.io.File

enum class SupportDirection {
    APP_TO_SUPPORT,
    SUPPORT_TO_APP,
    SYSTEM,
}

enum class SupportMessageType {
    TEXT,
    PHOTO,
    DOCUMENT,
    VIDEO,
    AUDIO,
    VOICE,
    FILE,
}

data class SupportChatMessage(
    val id: String,
    val direction: SupportDirection,
    val messageType: SupportMessageType,
    val text: String?,
    val fileName: String?,
    val mimeType: String?,
    val hasAttachment: Boolean,
    val createdAtRaw: String,
) {
    fun createdAtInstant(): Instant? = runCatching { Instant.parse(createdAtRaw) }.getOrNull()
}

data class SupportMessagesPage(
    val items: List<SupportChatMessage>,
    val unreadCount: Int,
)

data class DownloadedAttachment(
    val file: File,
    val mimeType: String,
)
