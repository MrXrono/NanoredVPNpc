package com.nanored.vpn.ui

import android.os.Bundle
import android.util.Log
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AlertDialog
import androidx.lifecycle.lifecycleScope
import com.nanored.vpn.AppConfig
import com.nanored.vpn.BuildConfig
import com.nanored.vpn.R
import com.nanored.vpn.databinding.ActivityCheckUpdateBinding
import com.nanored.vpn.dto.CheckUpdateResult
import com.nanored.vpn.extension.toast
import com.nanored.vpn.extension.toastError
import com.nanored.vpn.extension.toastSuccess
import com.nanored.vpn.handler.MmkvManager
import com.nanored.vpn.handler.UpdateCheckerManager
import com.nanored.vpn.handler.V2RayNativeManager
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

class CheckUpdateActivity : BaseActivity() {

    private val binding by lazy { ActivityCheckUpdateBinding.inflate(layoutInflater) }

    private val requestInstallPermission = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) {
        if (UpdateCheckerManager.canInstallApk(this)) {
            UpdateCheckerManager.installPendingApk(this)
        } else {
            toastError("Разрешение на установку не получено")
            UpdateCheckerManager.clearPendingApk()
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentViewWithToolbar(binding.root, showHomeAsUp = true, title = getString(R.string.update_check_for_update))

        binding.layoutCheckUpdate.setOnClickListener {
            checkForUpdates(binding.checkPreRelease.isChecked)
        }

        binding.checkPreRelease.setOnCheckedChangeListener { _, isChecked ->
            MmkvManager.encodeSettings(AppConfig.PREF_CHECK_UPDATE_PRE_RELEASE, isChecked)
        }
        binding.checkPreRelease.isChecked = MmkvManager.decodeSettingsBool(AppConfig.PREF_CHECK_UPDATE_PRE_RELEASE, false)

        "v${BuildConfig.VERSION_NAME} (${V2RayNativeManager.getLibVersion()})".also {
            binding.tvVersion.text = it
        }

        checkForUpdates(binding.checkPreRelease.isChecked)
    }

    private fun checkForUpdates(includePreRelease: Boolean) {
        toast(R.string.update_checking_for_update)
        showLoading()

        lifecycleScope.launch {
            try {
                val result = UpdateCheckerManager.checkForUpdate(includePreRelease)
                if (result.hasUpdate) {
                    showUpdateDialog(result)
                } else {
                    toastSuccess(R.string.update_already_latest_version)
                }
            } catch (e: Exception) {
                Log.e(AppConfig.TAG, "Failed to check for updates: ${e.message}")
                toastError(e.message ?: getString(R.string.toast_failure))
            }
            finally {
                hideLoading()
            }
        }
    }

    private fun showUpdateDialog(result: CheckUpdateResult) {
        AlertDialog.Builder(this)
            .setTitle(getString(R.string.update_new_version_found, result.latestVersion))
            .setMessage(result.releaseNotes)
            .setPositiveButton(R.string.update_now) { _, _ ->
                result.downloadUrl?.let { url ->
                    downloadAndInstallUpdate(url)
                }
            }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    private fun tryInstallApk(apkFile: java.io.File) {
        if (UpdateCheckerManager.canInstallApk(this)) {
            UpdateCheckerManager.installApk(this, apkFile)
        } else {
            UpdateCheckerManager.savePendingApk(apkFile)
            toast("Разрешите установку из этого источника")
            requestInstallPermission.launch(UpdateCheckerManager.getInstallPermissionIntent(this))
        }
    }

    private fun downloadAndInstallUpdate(downloadUrl: String) {
        toast("Загрузка обновления...")
        showLoading()
        lifecycleScope.launch(Dispatchers.IO) {
            try {
                val apkFile = UpdateCheckerManager.downloadApk(this@CheckUpdateActivity, downloadUrl)
                withContext(Dispatchers.Main) {
                    hideLoading()
                    if (apkFile != null) {
                        tryInstallApk(apkFile)
                    } else {
                        toastError("Ошибка загрузки обновления")
                    }
                }
            } catch (e: Exception) {
                withContext(Dispatchers.Main) {
                    hideLoading()
                    toastError("Ошибка: ${e.message}")
                }
            }
        }
    }
}
