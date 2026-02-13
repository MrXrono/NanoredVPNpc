package com.nanored.vpn.ui

import android.os.Bundle
import com.nanored.vpn.AppConfig
import com.nanored.vpn.BuildConfig
import com.nanored.vpn.R
import com.nanored.vpn.databinding.ActivityAboutBinding
import com.nanored.vpn.util.Utils

class AboutActivity : BaseActivity() {
    private val binding by lazy { ActivityAboutBinding.inflate(layoutInflater) }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentViewWithToolbar(binding.root, showHomeAsUp = true, title = getString(R.string.title_about))

        binding.layoutFeedback.setOnClickListener {
            Utils.openUri(this, AppConfig.APP_ISSUES_URL)
        }

        binding.layoutTgChannel.setOnClickListener {
            Utils.openUri(this, AppConfig.TG_CHANNEL_URL)
        }

        binding.tvVersion.text = "Nanored app v${BuildConfig.VERSION_NAME}"
        binding.tvAppId.text = ""
    }
}
