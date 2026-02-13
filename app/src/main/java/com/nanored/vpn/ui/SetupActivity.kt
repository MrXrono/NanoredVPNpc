package com.nanored.vpn.ui

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import androidx.appcompat.app.AppCompatActivity
import com.nanored.vpn.AppConfig
import com.nanored.vpn.R
import com.nanored.vpn.handler.MmkvManager
import com.google.android.material.button.MaterialButton

class SetupActivity : AppCompatActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        if (MmkvManager.decodeSubscriptions().isNotEmpty()) {
            startActivity(Intent(this, MainActivity::class.java))
            finish()
            return
        }

        setContentView(R.layout.activity_setup)

        findViewById<MaterialButton>(R.id.btn_open_bot).setOnClickListener {
            val intent = Intent(Intent.ACTION_VIEW, Uri.parse(AppConfig.APP_URL))
            startActivity(intent)
        }
    }

    override fun onResume() {
        super.onResume()
        if (MmkvManager.decodeSubscriptions().isNotEmpty()) {
            startActivity(Intent(this, MainActivity::class.java))
            finish()
        }
    }
}
