package com.nanored.vpn.support

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import android.util.LruCache
import java.util.concurrent.Executors

/**
 * Minimal image loader for support chat previews without pulling a heavy image library.
 * Caches small bitmaps in memory; decoding/downloading happens on a small background pool.
 */
object SupportChatImageLoader {
    private val executor = Executors.newFixedThreadPool(2)

    // 8MB cache for thumbnails.
    private val cache = object : LruCache<String, Bitmap>(8 * 1024 * 1024) {
        override fun sizeOf(key: String, value: Bitmap): Int = value.byteCount
    }

    fun load(
        context: Context,
        message: SupportChatMessage,
        maxWidthPx: Int,
        maxHeightPx: Int,
        onResult: (Bitmap?) -> Unit,
    ) {
        val key = cacheKey(message) ?: run {
            onResult(null)
            return
        }

        cache.get(key)?.let { bmp ->
            onResult(bmp)
            return
        }

        executor.execute {
            val bmp = runCatching {
                if (!message.localUri.isNullOrBlank()) {
                    decodeLocal(context, Uri.parse(message.localUri), maxWidthPx, maxHeightPx)
                } else {
                    val bytes = SupportChatApi.downloadAttachmentBytes(context, message.id)
                    if (bytes == null) null else decodeBytes(bytes, maxWidthPx, maxHeightPx)
                }
            }.getOrNull()

            if (bmp != null) cache.put(key, bmp)
            onResult(bmp)
        }
    }

    private fun cacheKey(message: SupportChatMessage): String? {
        if (!message.localUri.isNullOrBlank()) return "local:${message.localUri}"
        if (message.hasAttachment) return "remote:${message.id}"
        return null
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

