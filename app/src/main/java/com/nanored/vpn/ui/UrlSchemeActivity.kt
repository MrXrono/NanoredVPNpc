package com.nanored.vpn.ui

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.util.Log
import androidx.lifecycle.lifecycleScope
import com.nanored.vpn.AppConfig
import com.nanored.vpn.R
import com.nanored.vpn.databinding.ActivityLogcatBinding
import com.nanored.vpn.extension.toast
import com.nanored.vpn.extension.toastError
import com.nanored.vpn.handler.AngConfigManager
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.net.URLDecoder

class UrlSchemeActivity : BaseActivity() {
    private val binding by lazy { ActivityLogcatBinding.inflate(layoutInflater) }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(binding.root)

        try {
            intent.apply {
                if (action == Intent.ACTION_SEND) {
                    if ("text/plain" == type) {
                        intent.getStringExtra(Intent.EXTRA_TEXT)?.let {
                            parseUri(it, null)
                        }
                    }
                } else if (action == Intent.ACTION_VIEW) {
                    when {
                        data?.scheme == "nanored" && data?.host == "setup" -> {
                            handleNanoredSetup(data)
                        }

                        data?.host == "install-config" -> {
                            val uri: Uri? = intent.data
                            val shareUrl = uri?.getQueryParameter("url").orEmpty()
                            parseUri(shareUrl, uri?.fragment)
                        }

                        data?.host == "install-sub" -> {
                            val uri: Uri? = intent.data
                            val shareUrl = uri?.getQueryParameter("url").orEmpty()
                            parseUri(shareUrl, uri?.fragment)
                        }

                        else -> {
                            toastError(R.string.toast_failure)
                        }
                    }
                }
            }

            if (intent.data?.scheme != "nanored") {
                startActivity(Intent(this, MainActivity::class.java))
                finish()
            }
        } catch (e: Exception) {
            Log.e(AppConfig.TAG, "Error processing URL scheme", e)
        }
    }

    private fun handleNanoredSetup(data: Uri?) {
        val subUrl = data?.getQueryParameter("url")
        if (subUrl.isNullOrEmpty()) {
            toastError(R.string.toast_failure)
            startActivity(Intent(this, SetupActivity::class.java))
            finish()
            return
        }

        // Extract and save account_id (Telegram ID)
        val accountId = data?.getQueryParameter("account_id")
        if (!accountId.isNullOrEmpty()) {
            val prefs = getSharedPreferences("nanored_telemetry", MODE_PRIVATE)
            prefs.edit().putString("account_id", accountId).apply()
            Log.i(AppConfig.TAG, "Nanored setup: account_id=$accountId")
        }

        Log.i(AppConfig.TAG, "Nanored setup with sub URL: $subUrl")

        lifecycleScope.launch(Dispatchers.IO) {
            val (count, countSub) = AngConfigManager.importBatchConfig(subUrl, "", false)
            if (count + countSub <= 0) {
                AngConfigManager.updateConfigViaSubAll()
            }
            withContext(Dispatchers.Main) {
                toast(R.string.import_subscription_success)
                startActivity(Intent(this@UrlSchemeActivity, MainActivity::class.java).apply {
                    flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK
                })
                finish()
            }
        }
    }

    private fun parseUri(uriString: String?, fragment: String?) {
        if (uriString.isNullOrEmpty()) {
            return
        }
        Log.i(AppConfig.TAG, uriString)

        var decodedUrl = URLDecoder.decode(uriString, "UTF-8")
        val uri = Uri.parse(decodedUrl)
        if (uri != null) {
            if (uri.fragment.isNullOrEmpty() && !fragment.isNullOrEmpty()) {
                decodedUrl += "#${fragment}"
            }
            Log.i(AppConfig.TAG, decodedUrl)
            lifecycleScope.launch(Dispatchers.IO) {
                val (count, countSub) = AngConfigManager.importBatchConfig(decodedUrl, "", false)
                withContext(Dispatchers.Main) {
                    if (count + countSub > 0) {
                        toast(R.string.import_subscription_success)
                    } else {
                        toast(R.string.import_subscription_failure)
                    }
                }
            }
        }
    }
}
