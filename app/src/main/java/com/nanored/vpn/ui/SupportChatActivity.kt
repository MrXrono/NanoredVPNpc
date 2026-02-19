package com.nanored.vpn.ui

import android.content.ActivityNotFoundException
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import android.os.Bundle
import android.util.Log
import android.view.WindowManager
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AlertDialog
import androidx.core.content.FileProvider
import androidx.core.view.ViewCompat
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.updatePadding
import androidx.lifecycle.lifecycleScope
import androidx.recyclerview.widget.LinearLayoutManager
import com.nanored.vpn.AppConfig
import com.nanored.vpn.BuildConfig
import com.nanored.vpn.R
import com.nanored.vpn.databinding.ActivitySupportChatBinding
import com.nanored.vpn.extension.toast
import com.nanored.vpn.extension.toastError
import com.nanored.vpn.support.SupportChatAdapter
import com.nanored.vpn.support.SupportChatApi
import com.nanored.vpn.support.SupportChatMessage
import com.nanored.vpn.support.SupportDirection
import com.nanored.vpn.support.SupportMessageType
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlin.math.max
import java.time.Instant
import java.util.UUID
import android.provider.MediaStore
import java.io.File
import java.io.FileOutputStream
import java.net.URLConnection

class SupportChatActivity : BaseActivity() {
    private val binding by lazy { ActivitySupportChatBinding.inflate(layoutInflater) }
    private val adapter by lazy { SupportChatAdapter(::handleAttachmentClick) }
    private val allMessages = ArrayList<SupportChatMessage>()
    private var pollingJob: Job? = null

    // Some vendor ROMs (notably MIUI) can intermittently fail delivering results for picker intents.
    // Keep detailed logging here so we can see exactly what we got back.
    private val pickPhotosLauncher =
        registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
            try {
                val data = result.data
                val clip = data?.clipData
                val single = data?.data
	                Log.i(
	                    "SupportChatActivity",
	                    "pickPhotosLauncher: resultCode=${result.resultCode} data=${data != null} " +
                        "singleUri=$single clipCount=${clip?.itemCount ?: 0} extrasKeys=${data?.extras?.keySet()?.joinToString(",")}"
                )

                val uris = ArrayList<Uri>()
                if (clip != null) {
                    for (i in 0 until clip.itemCount) {
                        clip.getItemAt(i)?.uri?.let { uris.add(it) }
                    }
                } else if (single != null) {
                    uris.add(single)
                }

                if (uris.isEmpty()) {
                    Log.w("SupportChatActivity", "pickPhotosLauncher: empty selection")
                    return@registerForActivityResult
                }
                sendAttachments(uris.distinct())
            } catch (e: Exception) {
                Log.e("SupportChatActivity", "pickPhotosLauncher failed", e)
            }
        }

    private val pickFileLauncher = registerForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        uri?.let { sendAttachment(it) }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // We draw our own top bar, so handle system/IME insets manually.
        WindowCompat.setDecorFitsSystemWindows(window, false)
        window.setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE)
        setContentView(binding.root)

        binding.rvMessages.layoutManager = LinearLayoutManager(this).apply {
            stackFromEnd = true
        }
        binding.rvMessages.adapter = adapter

        binding.btnBack.setOnClickListener { onBackPressedDispatcher.onBackPressed() }
        binding.btnSend.setOnClickListener { sendText() }
        binding.btnAttach.setOnClickListener { showAttachmentChooser() }
        binding.btnSendLogsChat.setOnClickListener { sendLogs() }
        applyInsets()

        loadInitial()
    }

    override fun onResume() {
        super.onResume()
        startPolling()
    }

    override fun onPause() {
        pollingJob?.cancel()
        pollingJob = null
        super.onPause()
    }

    private fun loadInitial() {
        showLoading()
        lifecycleScope.launch(Dispatchers.IO) {
            try {
                val page = SupportChatApi.fetchMessages(this@SupportChatActivity)
                withContext(Dispatchers.Main) {
                    allMessages.clear()
                    allMessages.addAll(page.items)
                    adapter.submitList(allMessages.toList()) { scrollToBottom() }
                    hideLoading()
                }
                markReadIfNeeded()
            } catch (e: Exception) {
                Log.e(AppConfig.TAG, "Support chat load failed", e)
                withContext(Dispatchers.Main) {
                    hideLoading()
                    toastError(R.string.toast_failure)
                }
            }
        }
    }

    private fun startPolling() {
        if (pollingJob != null) return
        pollingJob = lifecycleScope.launch(Dispatchers.IO) {
            while (true) {
                try {
                    val afterId = lastServerMessageId()
                    val page = SupportChatApi.fetchMessages(this@SupportChatActivity, afterId = afterId)
                    if (page.items.isNotEmpty()) {
                        withContext(Dispatchers.Main) {
                            allMessages.addAll(page.items)
                            adapter.submitList(allMessages.toList()) { scrollToBottom() }
                        }
                        markReadIfNeeded()
                    }
                } catch (_: Exception) {
                }
                delay(6_000)
            }
        }
    }

    private fun sendText() {
        val text = binding.etMessage.text?.toString()?.trim().orEmpty()
        if (text.isEmpty()) return
        binding.etMessage.text?.clear()

        lifecycleScope.launch(Dispatchers.IO) {
            val chunks = splitForTelegram(text)
            for (chunk in chunks) {
                // Optimistic local echo so the user sees the message immediately.
                val pendingId = "local-${UUID.randomUUID()}"
                val pending = SupportChatMessage(
                    id = pendingId,
                    direction = SupportDirection.APP_TO_SUPPORT,
                    messageType = SupportMessageType.TEXT,
                    text = chunk,
                    fileName = null,
                    mimeType = null,
                    hasAttachment = false,
                    createdAtRaw = Instant.now().toString(),
                    localUri = null,
                    isPending = true,
                )
                withContext(Dispatchers.Main) {
                    allMessages.add(pending)
                    adapter.submitList(allMessages.toList()) { scrollToBottom() }
                }

                val sent = SupportChatApi.sendText(this@SupportChatActivity, chunk)
                withContext(Dispatchers.Main) {
                    if (sent == null) {
                        toastError(R.string.toast_failure)
                    } else {
                        // Replace optimistic message with the server-confirmed one.
                        val idx = allMessages.indexOfFirst { it.id == pendingId }
                        if (idx >= 0) allMessages.removeAt(idx)
                        allMessages.add(sent)
                        adapter.submitList(allMessages.toList()) { scrollToBottom() }
                    }
                }
                // small delay helps preserve order across network jitter
                delay(120)
            }
        }
    }

    private fun showAttachmentChooser() {
        val options = arrayOf(getString(R.string.support_chat_pick_photo), getString(R.string.support_chat_pick_file))
        AlertDialog.Builder(this)
            .setTitle(R.string.support_chat_attach_title)
            .setItems(options) { _, which ->
                when (which) {
                    0 -> {
                        // Prefer ACTION_PICK (gallery-style) with multi-select. This is more consistent on MIUI than GET_CONTENT.
                        val intent = Intent(
                            Intent.ACTION_PICK,
                            MediaStore.Images.Media.EXTERNAL_CONTENT_URI
                        ).apply {
                            type = "image/*"
                            putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true)
                            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                        }
                        Log.i("SupportChatActivity", "Launching photo picker: action=${intent.action} type=${intent.type}")
                        pickPhotosLauncher.launch(intent)
                    }
                    1 -> pickFileLauncher.launch(arrayOf("*/*"))
                }
            }
            .show()
    }

    private fun sendAttachments(uris: List<Uri>) {
        if (uris.isEmpty()) return
        // Send sequentially to preserve order and reduce pressure on the API.
        lifecycleScope.launch(Dispatchers.IO) {
            for (uri in uris) {
                val size = queryFileSize(uri)
                if (size != null && size > 50L * 1024L * 1024L) {
                    withContext(Dispatchers.Main) { toastError(getString(R.string.support_chat_file_too_large)) }
                    continue
                }

                val mimeType = contentResolver.getType(uri) ?: "application/octet-stream"
                val isPhoto = mimeType.startsWith("image/")
                val displayName = queryFileName(uri)
                val pendingId = "local-${UUID.randomUUID()}"
                val pending = SupportChatMessage(
                    id = pendingId,
                    direction = SupportDirection.APP_TO_SUPPORT,
                    messageType = if (isPhoto) SupportMessageType.PHOTO else SupportMessageType.DOCUMENT,
                    text = null,
                    fileName = displayName,
                    mimeType = mimeType,
                    hasAttachment = true,
                    createdAtRaw = Instant.now().toString(),
                    localUri = uri.toString(),
                    isPending = true,
                )
                withContext(Dispatchers.Main) {
                    allMessages.add(pending)
                    adapter.submitList(allMessages.toList()) { scrollToBottom() }
                }

                var uploadUri = uri
                var uploadName: String? = displayName
                var uploadMime = mimeType
                var tempFileToDelete: File? = null

                if (isPhoto) {
                    val prepared = preparePhotoForUpload(uri)
                    if (prepared != null) {
                        uploadUri = Uri.fromFile(prepared.file)
                        uploadName = prepared.file.name
                        uploadMime = "image/jpeg"
                        tempFileToDelete = prepared.file
                    }
                }

                val sent = SupportChatApi.sendFile(
                    context = this@SupportChatActivity,
                    fileUri = uploadUri,
                    overrideName = uploadName,
                    overrideMime = uploadMime,
                )
                tempFileToDelete?.delete()
                withContext(Dispatchers.Main) {
                    if (sent == null) {
                        toastError(R.string.toast_failure)
                    } else {
                        val idx = allMessages.indexOfFirst { it.id == pendingId }
                        if (idx >= 0) allMessages.removeAt(idx)
                        // Keep localUri for immediate preview even if server doesn't expose it later.
                        allMessages.add(
                            sent.copy(
                                localUri = pending.localUri,
                                fileName = sent.fileName ?: pending.fileName,
                                mimeType = sent.mimeType ?: pending.mimeType,
                            )
                        )
                        adapter.submitList(allMessages.toList()) { scrollToBottom() }
                    }
                }
                delay(120)
            }
        }
    }

    private fun sendAttachment(uri: Uri) {
        val size = queryFileSize(uri)
        if (size != null && size > 50L * 1024L * 1024L) {
            toastError(getString(R.string.support_chat_file_too_large))
            return
        }
        // Kept for backward callers; routing to multi-send.
        sendAttachments(listOf(uri))
    }

    private fun sendLogs() {
        toast(R.string.send_logs_sending)
        lifecycleScope.launch(Dispatchers.IO) {
            val logs = buildFullLogs()
            val sent = SupportChatApi.sendLogs(this@SupportChatActivity, logs)
            withContext(Dispatchers.Main) {
                if (sent == null) {
                    toastError(R.string.toast_failure)
                } else {
                    allMessages.add(sent)
                    adapter.submitList(allMessages.toList()) { scrollToBottom() }
                    toast(R.string.send_logs_success)
                }
            }
        }
    }

    private fun buildFullLogs(): String {
        val sb = StringBuilder()
        sb.appendLine("=== LOGCAT ===")
        try {
            val process = Runtime.getRuntime().exec(arrayOf("logcat", "-d"))
            val logcat = process.inputStream.bufferedReader().use { it.readText() }
            process.waitFor()
            sb.appendLine(logcat)
        } catch (e: Exception) {
            sb.appendLine("Logcat error: ${e.message}")
        }

        sb.appendLine("=== XRAY ACCESS LOG ===")
        val access = java.io.File(filesDir, "v2ray_access.log")
        sb.appendLine(if (access.exists()) access.readText() else "(no access log)")

        sb.appendLine("=== XRAY ERROR LOG ===")
        val error = java.io.File(filesDir, "v2ray_error.log")
        sb.appendLine(if (error.exists()) error.readText() else "(no error log)")
        return sb.toString().replace("\u0000", "")
    }

    private fun handleAttachmentClick(message: SupportChatMessage) {
        if (!message.localUri.isNullOrBlank() && message.id.startsWith("local-")) {
            openLocalAttachment(Uri.parse(message.localUri), message.mimeType)
            return
        }
        lifecycleScope.launch(Dispatchers.IO) {
            val download = SupportChatApi.downloadAttachment(
                context = this@SupportChatActivity,
                messageId = message.id,
                fallbackName = message.fileName,
            )
            withContext(Dispatchers.Main) {
                if (download == null) {
                    toastError(R.string.toast_failure)
                    return@withContext
                }
                openAttachment(download.file.toURI().toString(), download.file, download.mimeType)
            }
        }
    }

    private fun openLocalAttachment(uri: Uri, mimeType: String?) {
        try {
            val resolvedMime = resolveMimeType(mimeType, queryFileName(uri))
            val intent = Intent(Intent.ACTION_VIEW).apply {
                setDataAndType(uri, resolvedMime)
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            }
            startActivity(intent)
        } catch (e: ActivityNotFoundException) {
            try {
                val fallback = Intent(Intent.ACTION_VIEW).apply {
                    setDataAndType(uri, "*/*")
                    addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                }
                startActivity(fallback)
            } catch (_: Exception) {
                toast(uri.toString())
            }
        } catch (_: Exception) {
            toastError(R.string.toast_failure)
        }
    }

    private fun openAttachment(uriText: String, file: java.io.File, mimeType: String) {
        try {
            val uri = FileProvider.getUriForFile(this, "${BuildConfig.APPLICATION_ID}.cache", file)
            val resolvedMime = resolveMimeType(mimeType, file.name)
            val intent = Intent(Intent.ACTION_VIEW).apply {
                setDataAndType(uri, resolvedMime)
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            }
            startActivity(intent)
        } catch (e: ActivityNotFoundException) {
            try {
                val uri = FileProvider.getUriForFile(this, "${BuildConfig.APPLICATION_ID}.cache", file)
                val fallback = Intent(Intent.ACTION_VIEW).apply {
                    setDataAndType(uri, "*/*")
                    addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                }
                startActivity(fallback)
            } catch (_: Exception) {
                toast(uriText)
            }
        } catch (e: Exception) {
            toastError(R.string.toast_failure)
        }
    }

    private fun markReadIfNeeded() {
        val lastId = lastServerMessageId() ?: return
        lifecycleScope.launch(Dispatchers.IO) {
            SupportChatApi.markRead(this@SupportChatActivity, lastId)
        }
    }

    private fun lastServerMessageId(): String? {
        // Avoid passing optimistic "local-..." ids into the API. Server ids are opaque (UUID).
        return allMessages.asReversed().firstOrNull { !it.id.startsWith("local-") && it.id.isNotBlank() }?.id
    }

    private fun applyInsets() {
        val rootBaseBottom = binding.supportChatRoot.paddingBottom
        val topBaseTop = binding.topBar.paddingTop
        val composerBaseBottom = binding.composerBar.paddingBottom
        ViewCompat.setOnApplyWindowInsetsListener(binding.supportChatRoot) { _, insets ->
            val systemBars = insets.getInsets(WindowInsetsCompat.Type.systemBars())
            val imeInsets = insets.getInsets(WindowInsetsCompat.Type.ime())
            val bottom = max(systemBars.bottom, imeInsets.bottom)

            // Make room for status bar + IME so the UI doesn't get clipped/covered.
            binding.topBar.updatePadding(top = topBaseTop + systemBars.top)
            binding.supportChatRoot.updatePadding(bottom = rootBaseBottom)
            binding.composerBar.updatePadding(bottom = composerBaseBottom + bottom)
            insets
        }
        ViewCompat.requestApplyInsets(binding.supportChatRoot)
    }

    private fun scrollToBottom() {
        val lm = binding.rvMessages.layoutManager as? LinearLayoutManager ?: return
        // submitList() updates may apply asynchronously; post to ensure layout is ready.
        binding.rvMessages.post {
            val last = (adapter.itemCount - 1).coerceAtLeast(0)
            lm.scrollToPositionWithOffset(last, 0)
        }
    }

    private fun queryFileSize(uri: Uri): Long? {
        return try {
            contentResolver.query(uri, null, null, null, null)?.use { cursor ->
                val idx = cursor.getColumnIndex(android.provider.OpenableColumns.SIZE)
                if (idx >= 0 && cursor.moveToFirst()) cursor.getLong(idx) else null
            }
        } catch (_: Exception) {
            null
        }
    }

    private fun queryFileName(uri: Uri): String? {
        return try {
            contentResolver.query(uri, null, null, null, null)?.use { cursor ->
                val idx = cursor.getColumnIndex(android.provider.OpenableColumns.DISPLAY_NAME)
                if (idx >= 0 && cursor.moveToFirst()) cursor.getString(idx) else null
            }
        } catch (_: Exception) {
            null
        }
    }

    private fun splitForTelegram(text: String): List<String> {
        // Telegram hard limit is 4096 chars per message; keep a margin for headers/encoding.
        val maxLen = 3500
        if (text.length <= maxLen) return listOf(text)

        val parts = ArrayList<String>()
        var idx = 0
        while (idx < text.length) {
            val end = minOf(text.length, idx + maxLen)
            val slice = text.substring(idx, end)
            // Try to cut on newline within the last 300 chars of this slice.
            val cut = slice.lastIndexOf('\n').takeIf { it >= max(0, slice.length - 300) } ?: -1
            if (cut > 0) {
                parts.add(slice.substring(0, cut).trimEnd())
                idx += cut + 1
            } else {
                parts.add(slice)
                idx = end
            }
        }
        return parts.filter { it.isNotBlank() }
    }

    private data class PreparedPhoto(val file: File)

    private fun preparePhotoForUpload(uri: Uri): PreparedPhoto? {
        return try {
            val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
            contentResolver.openInputStream(uri)?.use { BitmapFactory.decodeStream(it, null, bounds) }
            if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return null

            val maxSide = 1920
            var sample = 1
            while ((bounds.outWidth / sample) > maxSide || (bounds.outHeight / sample) > maxSide) {
                sample *= 2
            }

            val decodeOpts = BitmapFactory.Options().apply { inSampleSize = sample.coerceAtLeast(1) }
            val bmp = contentResolver.openInputStream(uri)?.use { BitmapFactory.decodeStream(it, null, decodeOpts) }
                ?: return null

            val out = File(cacheDir, "support_photo_${System.currentTimeMillis()}.jpg")
            val targetBytes = 900 * 1024
            val qualitySteps = intArrayOf(85, 75, 65, 55, 45, 35)

            for (quality in qualitySteps) {
                FileOutputStream(out, false).use { fos ->
                    bmp.compress(Bitmap.CompressFormat.JPEG, quality, fos)
                    fos.flush()
                }
                if (out.length() <= targetBytes) break
            }
            PreparedPhoto(out)
        } catch (e: Exception) {
            Log.w("SupportChatActivity", "Photo pre-compress failed", e)
            null
        }
    }

    private fun resolveMimeType(rawMime: String?, fileName: String?): String {
        val normalized = rawMime?.substringBefore(";")?.trim()?.lowercase()
        if (!normalized.isNullOrBlank() && normalized != "application/octet-stream") return normalized
        if (!fileName.isNullOrBlank()) {
            URLConnection.guessContentTypeFromName(fileName)?.let { return it }
        }
        return "*/*"
    }
}
