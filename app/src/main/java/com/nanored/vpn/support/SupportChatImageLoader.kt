package com.nanored.vpn.support

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.media.MediaMetadataRetriever
import android.net.Uri
import android.util.Log
import android.util.LruCache
import java.util.concurrent.Executors

/**
 * Minimal media preview loader for support chat without pulling a heavy image library.
 * Caches small bitmaps in memory; decoding/downloading happens on a small background pool.
 */
object SupportChatImageLoader {
    private const val TAG = "SupportChatImageLoader"
    private val executor = Executors.newFixedThreadPool(2)

    // 8MB cache for thumbnails.
    private val cache = object : LruCache<String, Bitmap>(8 * 1024 * 1024) {
        override fun sizeOf(key: String, value: Bitmap): Int = value.byteCount
    }

    data class MediaPreview(
        val bitmap: Bitmap?,
        val durationSec: Int? = null,
    )

    fun loadPreview(
        context: Context,
        message: SupportChatMessage,
        maxWidthPx: Int,
        maxHeightPx: Int,
        onResult: (MediaPreview?) -> Unit,
    ) {
        val key = cacheKey(message) ?: run {
            onResult(null)
            return
        }

        cache.get(key)?.let { bmp ->
            val duration = if (message.messageType == SupportMessageType.VIDEO) {
                loadDurationSec(context, message)
            } else null
            onResult(MediaPreview(bitmap = bmp, durationSec = duration))
            return
        }

        executor.execute {
            val preview = runCatching {
                when (message.messageType) {
                    SupportMessageType.VIDEO -> loadVideoPreview(context, message, maxWidthPx, maxHeightPx)
                    else -> loadImagePreview(context, message, maxWidthPx, maxHeightPx)
                }
            }.onFailure {
                Log.w(TAG, "Preview load failed for message=${message.id}", it)
            }.getOrNull()

            preview?.bitmap?.let { cache.put(key, it) }
            onResult(preview)
        }
    }

    private fun cacheKey(message: SupportChatMessage): String? {
        if (!message.localUri.isNullOrBlank()) return "local:${message.localUri}"
        if (message.hasAttachment) return "remote:${message.id}"
        return null
    }

    private fun loadImagePreview(
        context: Context,
        message: SupportChatMessage,
        maxWidthPx: Int,
        maxHeightPx: Int,
    ): MediaPreview? {
        val bmp = if (!message.localUri.isNullOrBlank()) {
            decodeLocal(context, Uri.parse(message.localUri), maxWidthPx, maxHeightPx)
        } else {
            val bytes = SupportChatApi.downloadAttachmentBytes(context, message.id)
            if (bytes == null) null else decodeBytes(bytes, maxWidthPx, maxHeightPx)
        }
        return MediaPreview(bitmap = bmp, durationSec = null)
    }

    private fun loadVideoPreview(
        context: Context,
        message: SupportChatMessage,
        maxWidthPx: Int,
        maxHeightPx: Int,
    ): MediaPreview? {
        val sourceUri = if (!message.localUri.isNullOrBlank()) {
            Uri.parse(message.localUri)
        } else {
            val downloaded = SupportChatApi.downloadAttachment(context, message.id, message.fileName) ?: return null
            Uri.fromFile(downloaded.file)
        }

        val retriever = MediaMetadataRetriever()
        return try {
            retriever.setDataSource(context, sourceUri)
            val frame = retriever.getFrameAtTime(0, MediaMetadataRetriever.OPTION_CLOSEST_SYNC) ?: return null
            val durationMs = retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_DURATION)
                ?.toLongOrNull()
                ?.coerceAtLeast(0L)
                ?: 0L
            val scaled = scaleBitmap(frame, maxWidthPx, maxHeightPx)
            MediaPreview(bitmap = scaled, durationSec = (durationMs / 1000L).toInt())
        } finally {
            runCatching { retriever.release() }
        }
    }

    private fun loadDurationSec(context: Context, message: SupportChatMessage): Int? {
        return runCatching {
            if (!message.localUri.isNullOrBlank()) {
                val retriever = MediaMetadataRetriever()
                try {
                    retriever.setDataSource(context, Uri.parse(message.localUri))
                    retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_DURATION)
                        ?.toLongOrNull()
                        ?.let { (it / 1000L).toInt() }
                } finally {
                    runCatching { retriever.release() }
                }
            } else null
        }.getOrNull()
    }

    private fun decodeLocal(context: Context, uri: Uri, maxW: Int, maxH: Int): Bitmap? {
        val resolver = context.contentResolver
        // First pass: bounds.
        val optsBounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        resolver.openInputStream(uri)?.use { BitmapFactory.decodeStream(it, null, optsBounds) }
        if (optsBounds.outWidth <= 0 || optsBounds.outHeight <= 0) return null

        val sample = computeInSampleSize(optsBounds.outWidth, optsBounds.outHeight, maxW, maxH)
        val opts = BitmapFactory.Options().apply { inSampleSize = sample }
        resolver.openInputStream(uri)?.use { input ->
            return BitmapFactory.decodeStream(input, null, opts)
        }
        return null
    }

    private fun decodeBytes(bytes: ByteArray, maxW: Int, maxH: Int): Bitmap? {
        val optsBounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeByteArray(bytes, 0, bytes.size, optsBounds)
        if (optsBounds.outWidth <= 0 || optsBounds.outHeight <= 0) return null

        val sample = computeInSampleSize(optsBounds.outWidth, optsBounds.outHeight, maxW, maxH)
        val opts = BitmapFactory.Options().apply { inSampleSize = sample }
        return BitmapFactory.decodeByteArray(bytes, 0, bytes.size, opts)
    }

    private fun scaleBitmap(src: Bitmap, maxW: Int, maxH: Int): Bitmap {
        if (src.width <= maxW && src.height <= maxH) return src
        val ratio = minOf(maxW.toFloat() / src.width.toFloat(), maxH.toFloat() / src.height.toFloat())
        val w = (src.width * ratio).toInt().coerceAtLeast(1)
        val h = (src.height * ratio).toInt().coerceAtLeast(1)
        return Bitmap.createScaledBitmap(src, w, h, true)
    }

    private fun computeInSampleSize(srcW: Int, srcH: Int, reqW: Int, reqH: Int): Int {
        var inSampleSize = 1
        if (srcH > reqH || srcW > reqW) {
            var halfH = srcH / 2
            var halfW = srcW / 2
            while ((halfH / inSampleSize) >= reqH && (halfW / inSampleSize) >= reqW) {
                inSampleSize *= 2
            }
        }
        return inSampleSize.coerceAtLeast(1)
    }
}
