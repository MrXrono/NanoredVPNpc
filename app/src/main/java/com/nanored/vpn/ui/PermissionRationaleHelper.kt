package com.nanored.vpn.ui

import android.Manifest
import android.content.SharedPreferences
import android.content.pm.PackageManager
import android.os.Build
import android.view.LayoutInflater
import androidx.activity.result.ActivityResultLauncher
import androidx.appcompat.app.AlertDialog
import androidx.core.content.ContextCompat
import com.nanored.vpn.R
import com.nanored.vpn.handler.UpdateCheckerManager

/**
 * Shows a one-time permission rationale dialog on first launch,
 * then sequentially requests runtime permissions and install-packages permission.
 */
object PermissionRationaleHelper {

    private const val PREF_NAME = "perm_rationale"
    private const val KEY_SHOWN = "dialog_shown"

    private fun prefs(activity: MainActivity): SharedPreferences {
        return activity.getSharedPreferences(PREF_NAME, 0)
    }

    /**
     * Returns true if all runtime permissions are already granted
     * (so we don't need to show the dialog).
     */
    private fun allPermissionsGranted(activity: MainActivity): Boolean {
        val perms = mutableListOf(
            Manifest.permission.CAMERA
        )
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            perms.add(Manifest.permission.READ_MEDIA_IMAGES)
            perms.add(Manifest.permission.POST_NOTIFICATIONS)
        } else {
            perms.add(Manifest.permission.READ_EXTERNAL_STORAGE)
        }
        val runtimeOk = perms.all {
            ContextCompat.checkSelfPermission(activity, it) == PackageManager.PERMISSION_GRANTED
        }
        val installOk = UpdateCheckerManager.canInstallApk(activity)
        return runtimeOk && installOk
    }

    /**
     * Call from MainActivity.onCreate().
     * Shows the rationale dialog once, then requests permissions.
     */
    fun showIfNeeded(
        activity: MainActivity,
        multiPermissionLauncher: ActivityResultLauncher<Array<String>>,
        installPermissionLauncher: ActivityResultLauncher<android.content.Intent>
    ) {
        if (prefs(activity).getBoolean(KEY_SHOWN, false)) return
        if (allPermissionsGranted(activity)) {
            prefs(activity).edit().putBoolean(KEY_SHOWN, true).apply()
            return
        }

        val dialogView = LayoutInflater.from(activity)
            .inflate(R.layout.dialog_permissions, null)

        AlertDialog.Builder(activity)
            .setView(dialogView)
            .setCancelable(false)
            .setPositiveButton(R.string.perm_dialog_accept) { dialog, _ ->
                dialog.dismiss()
                prefs(activity).edit().putBoolean(KEY_SHOWN, true).apply()
                requestAllPermissions(activity, multiPermissionLauncher, installPermissionLauncher)
            }
            .show()
    }

    private fun requestAllPermissions(
        activity: MainActivity,
        multiPermissionLauncher: ActivityResultLauncher<Array<String>>,
        installPermissionLauncher: ActivityResultLauncher<android.content.Intent>
    ) {
        // Collect runtime permissions that are not yet granted
        val needed = mutableListOf<String>()

        if (ContextCompat.checkSelfPermission(activity, Manifest.permission.CAMERA)
            != PackageManager.PERMISSION_GRANTED
        ) {
            needed.add(Manifest.permission.CAMERA)
        }

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            if (ContextCompat.checkSelfPermission(activity, Manifest.permission.READ_MEDIA_IMAGES)
                != PackageManager.PERMISSION_GRANTED
            ) {
                needed.add(Manifest.permission.READ_MEDIA_IMAGES)
            }
            if (ContextCompat.checkSelfPermission(activity, Manifest.permission.POST_NOTIFICATIONS)
                != PackageManager.PERMISSION_GRANTED
            ) {
                needed.add(Manifest.permission.POST_NOTIFICATIONS)
            }
        } else {
            if (ContextCompat.checkSelfPermission(activity, Manifest.permission.READ_EXTERNAL_STORAGE)
                != PackageManager.PERMISSION_GRANTED
            ) {
                needed.add(Manifest.permission.READ_EXTERNAL_STORAGE)
            }
        }

        if (needed.isNotEmpty()) {
            // The callback in MainActivity will chain the install permission request
            multiPermissionLauncher.launch(needed.toTypedArray())
        } else {
            // All runtime permissions granted, check install permission
            requestInstallIfNeeded(activity, installPermissionLauncher)
        }
    }

    /**
     * Called after runtime permissions are processed.
     * Requests the install-packages permission if needed.
     */
    fun requestInstallIfNeeded(
        activity: MainActivity,
        installPermissionLauncher: ActivityResultLauncher<android.content.Intent>
    ) {
        if (!UpdateCheckerManager.canInstallApk(activity)) {
            installPermissionLauncher.launch(UpdateCheckerManager.getInstallPermissionIntent(activity))
        }
    }
}
