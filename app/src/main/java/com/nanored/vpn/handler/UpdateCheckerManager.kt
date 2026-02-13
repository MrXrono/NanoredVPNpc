package com.nanored.vpn.handler

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.provider.Settings
import android.util.Log
import androidx.core.content.FileProvider
import com.nanored.vpn.AppConfig
import com.nanored.vpn.BuildConfig
import com.nanored.vpn.dto.CheckUpdateResult
import com.nanored.vpn.util.HttpUtil
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.io.FileOutputStream

object UpdateCheckerManager {
    suspend fun checkForUpdate(includePreRelease: Boolean = false): CheckUpdateResult = withContext(Dispatchers.IO) {
        val url = AppConfig.APP_API_URL
        if (url.isEmpty()) {
            return@withContext CheckUpdateResult(hasUpdate = false)
        }

        var response = HttpUtil.getUrlContent(url, 5000)
        if (response.isNullOrEmpty()) {
            val httpPort = SettingsManager.getHttpPort()
            response = HttpUtil.getUrlContent(url, 5000, httpPort)
                ?: return@withContext CheckUpdateResult(hasUpdate = false)
        }

        val fields = mutableMapOf<String, String>()
        response.lines().forEach { line ->
            val idx = line.indexOf('=')
            if (idx > 0) {
                fields[line.substring(0, idx).trim()] = line.substring(idx + 1).trim()
            }
        }

        val latestVersion = fields["version"] ?: return@withContext CheckUpdateResult(hasUpdate = false)
        val downloadUrl = fields["url"] ?: return@withContext CheckUpdateResult(hasUpdate = false)
        val changelog = (fields["changelog"] ?: "").replace("\\n", "\n")

        Log.i(AppConfig.TAG, "Update check: remote=$latestVersion current=${BuildConfig.VERSION_NAME}")

        return@withContext if (compareVersions(latestVersion, BuildConfig.VERSION_NAME) > 0) {
            CheckUpdateResult(
                hasUpdate = true,
                latestVersion = latestVersion,
                releaseNotes = changelog,
                downloadUrl = downloadUrl
            )
        } else {
            CheckUpdateResult(hasUpdate = false)
        }
    }

    suspend fun downloadApk(context: Context, downloadUrl: String): File? = withContext(Dispatchers.IO) {
        try {
            // Try direct connection first (VPN may not be running)
            var connection = HttpUtil.createProxyConnection(downloadUrl, 0, 30000, 120000, true)
            if (connection == null) {
                val httpPort = SettingsManager.getHttpPort()
                connection = HttpUtil.createProxyConnection(downloadUrl, httpPort, 30000, 120000, true)
                    ?: throw IllegalStateException("Failed to create connection")
            }

            try {
                val apkFile = File(context.cacheDir, "update.apk")
                Log.i(AppConfig.TAG, "Downloading APK to: ${apkFile.absolutePath}")

                FileOutputStream(apkFile).use { outputStream ->
                    connection.inputStream.use { inputStream ->
                        inputStream.copyTo(outputStream, 8192)
                    }
                }
                Log.i(AppConfig.TAG, "APK download completed: ${apkFile.length()} bytes")
                return@withContext apkFile
            } catch (e: Exception) {
                Log.e(AppConfig.TAG, "Failed to download APK: ${e.message}")
                return@withContext null
            } finally {
                try {
                    connection.disconnect()
                } catch (e: Exception) {
                    Log.e(AppConfig.TAG, "Error closing connection: ${e.message}")
                }
            }
        } catch (e: Exception) {
            Log.e(AppConfig.TAG, "Failed to initiate download: ${e.message}")
            return@withContext null
        }
    }

    /** Cached APK file for installation after permission grant */
    var pendingApkFile: File? = null
        private set

    fun canInstallApk(context: Context): Boolean {
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            context.packageManager.canRequestPackageInstalls()
        } else {
            true
        }
    }

    fun getInstallPermissionIntent(context: Context): Intent {
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:${context.packageName}"))
        } else {
            Intent(Settings.ACTION_SECURITY_SETTINGS)
        }
    }

    fun savePendingApk(apkFile: File) {
        pendingApkFile = apkFile
    }

    fun installApk(context: Context, apkFile: File) {
        pendingApkFile = apkFile
        val uri = FileProvider.getUriForFile(context, "${BuildConfig.APPLICATION_ID}.cache", apkFile)
        val intent = Intent(Intent.ACTION_VIEW).apply {
            setDataAndType(uri, "application/vnd.android.package-archive")
            flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_GRANT_READ_URI_PERMISSION
        }
        context.startActivity(intent)
    }

    fun installPendingApk(context: Context) {
        pendingApkFile?.let { installApk(context, it) }
    }

    fun clearPendingApk() {
        pendingApkFile = null
    }

    private fun compareVersions(version1: String, version2: String): Int {
        val v1 = version1.split(".")
        val v2 = version2.split(".")

        for (i in 0 until maxOf(v1.size, v2.size)) {
            val num1 = if (i < v1.size) v1[i].toIntOrNull() ?: 0 else 0
            val num2 = if (i < v2.size) v2[i].toIntOrNull() ?: 0 else 0
            if (num1 != num2) return num1 - num2
        }
        return 0
    }
}
