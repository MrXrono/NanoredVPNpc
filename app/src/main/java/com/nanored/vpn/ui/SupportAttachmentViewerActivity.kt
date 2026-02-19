package com.nanored.vpn.ui

import android.content.ActivityNotFoundException
import android.content.Intent
import android.graphics.BitmapFactory
import android.net.Uri
import android.os.Bundle
import android.util.Log
import android.widget.MediaController
import androidx.core.net.toUri
import androidx.lifecycle.lifecycleScope
import com.nanored.vpn.R
import com.nanored.vpn.databinding.ActivitySupportAttachmentViewerBinding
import com.nanored.vpn.extension.toastError
import com.nanored.vpn.support.SupportMessageType
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.ByteArrayOutputStream
import java.io.File
import java.net.URLConnection
import java.nio.charset.Charset

class SupportAttachmentViewerActivity : BaseActivity() {
    private val binding by lazy { ActivitySupportAttachmentViewerBinding.inflate(layoutInflater) }

    private var attachmentUri: Uri? = null
    private var mimeType: String? = null
    private var fileName: String? = null
    private var messageType: SupportMessageType = SupportMessageType.FILE

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(binding.root)

        attachmentUri = intent.getStringExtra(EXTRA_URI)?.toUri()
        mimeType = intent.getStringExtra(EXTRA_MIME)
        fileName = intent.getStringExtra(EXTRA_FILE_NAME)
        messageType = runCatching {
            SupportMessageType.valueOf(intent.getStringExtra(EXTRA_MESSAGE_TYPE).orEmpty())
        }.getOrDefault(SupportMessageType.FILE)

        binding.btnBack.setOnClickListener { finish() }
        binding.btnOpenExternal.setOnClickListener { openExternal() }
        binding.tvTitle.text = fileName?.ifBlank { null } ?: getString(R.string.support_chat_viewer_title)

        render()
    }

    override fun onPause() {
        super.onPause()
        binding.videoView.pause()
    }

    override fun onDestroy() {
        runCatching { binding.videoView.stopPlayback() }
        super.onDestroy()
    }

    private fun render() {
        val uri = attachmentUri
        if (uri == null) {
            toastError(R.string.toast_failure)
            finish()
            return
        }

        val resolvedMime = resolveMimeType(mimeType, fileName)
        when {
            isImage(resolvedMime, messageType) -> renderImage(uri)
            isVideo(resolvedMime, messageType) -> renderVideo(uri, audioMode = false)
            isAudio(resolvedMime, messageType) -> renderVideo(uri, audioMode = true)
            isTextLike(resolvedMime, fileName) -> renderText(uri)
            else -> renderUnsupported()
        }
    }

    private fun showOnly(mode: String) {
        binding.progress.visibility = android.view.View.GONE
        binding.ivPreview.visibility = if (mode == "image") android.view.View.VISIBLE else android.view.View.GONE
        binding.videoView.visibility = if (mode == "video" || mode == "audio") android.view.View.VISIBLE else android.view.View.GONE
        binding.audioHint.visibility = if (mode == "audio") android.view.View.VISIBLE else android.view.View.GONE
        binding.textWrap.visibility = if (mode == "text") android.view.View.VISIBLE else android.view.View.GONE
        binding.unsupportedWrap.visibility = if (mode == "unsupported") android.view.View.VISIBLE else android.view.View.GONE
    }

    private fun renderImage(uri: Uri) {
        lifecycleScope.launch(Dispatchers.IO) {
            val bmp = runCatching {
                openInputStream(uri)?.use { BitmapFactory.decodeStream(it) }
            }.getOrNull()
            withContext(Dispatchers.Main) {
                if (bmp == null) {
                    renderUnsupported()
                    return@withContext
                }
                showOnly("image")
                binding.ivPreview.setImageBitmap(bmp)
            }
        }
    }

    private fun renderVideo(uri: Uri, audioMode: Boolean) {
        showOnly(if (audioMode) "audio" else "video")
        val controller = MediaController(this)
        controller.setAnchorView(binding.videoView)
        binding.videoView.setMediaController(controller)
        binding.videoView.setVideoURI(uri)
        binding.videoView.setOnPreparedListener { mediaPlayer ->
            binding.progress.visibility = android.view.View.GONE
            if (audioMode) {
                // keep simple inline player UI for audio files
                mediaPlayer.setVideoScalingMode(android.media.MediaPlayer.VIDEO_SCALING_MODE_SCALE_TO_FIT)
            }
            binding.videoView.start()
            controller.show(0)
        }
        binding.videoView.setOnErrorListener { _, _, _ ->
            renderUnsupported()
            true
        }
    }

    private fun renderText(uri: Uri) {
        lifecycleScope.launch(Dispatchers.IO) {
            val text = runCatching { readTextPreview(uri) }.getOrNull()
            withContext(Dispatchers.Main) {
                if (text == null) {
                    renderUnsupported()
                    return@withContext
                }
                showOnly("text")
                binding.tvText.text = text
            }
        }
    }

    private fun renderUnsupported() {
        showOnly("unsupported")
        val info = buildString {
            append(getString(R.string.support_chat_viewer_not_supported))
            if (!fileName.isNullOrBlank()) append("\n\n$fileName")
            if (!mimeType.isNullOrBlank()) append("\n${mimeType}")
        }
        binding.tvUnsupported.text = info
    }

    private fun openExternal() {
        val uri = attachmentUri ?: return
        val mime = resolveMimeType(mimeType, fileName)
        try {
            startActivity(
                Intent(Intent.ACTION_VIEW).apply {
                    setDataAndType(uri, mime)
                    addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                }
            )
        } catch (_: ActivityNotFoundException) {
            try {
                startActivity(
                    Intent(Intent.ACTION_VIEW).apply {
                        setDataAndType(uri, "*/*")
                        addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                    }
                )
            } catch (e: Exception) {
                Log.e("SupportAttachmentViewer", "openExternal failed", e)
                toastError(R.string.toast_failure)
            }
        }
    }

    private fun openInputStream(uri: Uri) =
        if (uri.scheme.equals("file", ignoreCase = true)) {
            val file = File(uri.path ?: return null)
            file.inputStream()
        } else {
            contentResolver.openInputStream(uri)
        }

    private fun readTextPreview(uri: Uri): String {
        val maxBytes = 2 * 1024 * 1024
        val data = openInputStream(uri)?.use { input ->
            val buffer = ByteArray(8 * 1024)
            val out = ByteArrayOutputStream()
            var total = 0
            while (true) {
                val read = input.read(buffer)
                if (read <= 0) break
                val remaining = maxBytes - total
                if (remaining <= 0) break
                val writeLen = minOf(read, remaining)
                out.write(buffer, 0, writeLen)
                total += writeLen
                if (total >= maxBytes) break
            }
            out.toByteArray()
        } ?: return ""

        return decodeText(data)
    }

    private fun decodeText(bytes: ByteArray): String {
        val utf = runCatching { bytes.toString(Charsets.UTF_8) }.getOrDefault("")
        if (!utf.contains('\uFFFD')) return utf
        val cp1251 = runCatching { bytes.toString(Charset.forName("windows-1251")) }.getOrDefault(utf)
        return cp1251
    }

    private fun resolveMimeType(rawMime: String?, guessedName: String?): String {
        val normalized = rawMime?.substringBefore(";")?.trim()?.lowercase()
        if (!normalized.isNullOrBlank() && normalized != "application/octet-stream") return normalized
        if (!guessedName.isNullOrBlank()) {
            URLConnection.guessContentTypeFromName(guessedName)?.let { return it.lowercase() }
        }
        return "application/octet-stream"
    }

    private fun isImage(mime: String, type: SupportMessageType): Boolean =
        type == SupportMessageType.PHOTO || mime.startsWith("image/")

    private fun isVideo(mime: String, type: SupportMessageType): Boolean =
        type == SupportMessageType.VIDEO || mime.startsWith("video/")

    private fun isAudio(mime: String, type: SupportMessageType): Boolean =
        type == SupportMessageType.AUDIO || type == SupportMessageType.VOICE || mime.startsWith("audio/")

    private fun isTextLike(mime: String, name: String?): Boolean {
        if (mime.startsWith("text/")) return true
        if (mime in setOf("application/json", "application/xml", "application/x-yaml")) return true
        val ext = name?.substringAfterLast('.', "")?.lowercase().orEmpty()
        return ext in setOf("txt", "log", "json", "xml", "yaml", "yml", "ini", "cfg", "conf", "md", "csv")
    }

    companion object {
        private const val EXTRA_URI = "extra_uri"
        private const val EXTRA_MIME = "extra_mime"
        private const val EXTRA_FILE_NAME = "extra_file_name"
        private const val EXTRA_MESSAGE_TYPE = "extra_message_type"

        fun createIntent(
            context: android.content.Context,
            uri: Uri,
            mimeType: String?,
            fileName: String?,
            messageType: SupportMessageType,
        ): Intent = Intent(context, SupportAttachmentViewerActivity::class.java).apply {
            putExtra(EXTRA_URI, uri.toString())
            putExtra(EXTRA_MIME, mimeType)
            putExtra(EXTRA_FILE_NAME, fileName)
            putExtra(EXTRA_MESSAGE_TYPE, messageType.name)
        }
    }
}
