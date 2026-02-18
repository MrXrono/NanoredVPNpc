package com.nanored.vpn.ui

import android.content.ActivityNotFoundException
import android.content.Intent
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
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlin.math.max

class SupportChatActivity : BaseActivity() {
    private val binding by lazy { ActivitySupportChatBinding.inflate(layoutInflater) }
    private val adapter by lazy { SupportChatAdapter(::handleAttachmentClick) }
    private val allMessages = ArrayList<SupportChatMessage>()
    private var pollingJob: Job? = null

    private val pickPhotoLauncher = registerForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        uri?.let { sendAttachment(it) }
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
                    adapter.submitList(allMessages.toList())
                    scrollToBottom()
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
                    val afterId = allMessages.lastOrNull()?.id
                    val page = SupportChatApi.fetchMessages(this@SupportChatActivity, afterId = afterId)
                    if (page.items.isNotEmpty()) {
                        withContext(Dispatchers.Main) {
                            allMessages.addAll(page.items)
                            adapter.submitList(allMessages.toList())
                            scrollToBottom()
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
            val sent = SupportChatApi.sendText(this@SupportChatActivity, text)
            withContext(Dispatchers.Main) {
                if (sent == null) {
                    toastError(R.string.toast_failure)
                } else {
                    allMessages.add(sent)
                    adapter.submitList(allMessages.toList())
                    scrollToBottom()
                }
            }
        }
    }

    private fun showAttachmentChooser() {
        val options = arrayOf(getString(R.string.support_chat_pick_photo), getString(R.string.support_chat_pick_file))
        AlertDialog.Builder(this)
            .setTitle(R.string.support_chat_attach_title)
            .setItems(options) { _, which ->
                when (which) {
                    0 -> pickPhotoLauncher.launch(arrayOf("image/*"))
                    1 -> pickFileLauncher.launch(arrayOf("*/*"))
                }
            }
            .show()
    }

    private fun sendAttachment(uri: Uri) {
        lifecycleScope.launch(Dispatchers.IO) {
            val sent = SupportChatApi.sendFile(this@SupportChatActivity, uri)
            withContext(Dispatchers.Main) {
                if (sent == null) {
                    toastError(R.string.toast_failure)
                } else {
                    allMessages.add(sent)
                    adapter.submitList(allMessages.toList())
                    scrollToBottom()
                }
            }
        }
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
                    adapter.submitList(allMessages.toList())
                    scrollToBottom()
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

    private fun openAttachment(uriText: String, file: java.io.File, mimeType: String) {
        try {
            val uri = FileProvider.getUriForFile(this, "${BuildConfig.APPLICATION_ID}.cache", file)
            val intent = Intent(Intent.ACTION_VIEW).apply {
                setDataAndType(uri, mimeType)
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            }
            startActivity(intent)
        } catch (e: ActivityNotFoundException) {
            toast(uriText)
        } catch (e: Exception) {
            toastError(R.string.toast_failure)
        }
    }

    private fun markReadIfNeeded() {
        val lastId = allMessages.lastOrNull()?.id ?: return
        lifecycleScope.launch(Dispatchers.IO) {
            SupportChatApi.markRead(this@SupportChatActivity, lastId)
        }
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
            binding.supportChatRoot.updatePadding(bottom = rootBaseBottom + bottom)
            binding.composerBar.updatePadding(bottom = composerBaseBottom)
            insets
        }
        ViewCompat.requestApplyInsets(binding.supportChatRoot)
    }

    private fun scrollToBottom() {
        if (allMessages.isNotEmpty()) {
            binding.rvMessages.scrollToPosition(allMessages.size - 1)
        }
    }
}
