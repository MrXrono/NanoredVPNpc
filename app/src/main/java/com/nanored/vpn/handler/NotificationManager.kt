package com.nanored.vpn.handler

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.graphics.Color
import android.os.Build
import androidx.annotation.RequiresApi
import androidx.core.app.NotificationCompat
import com.nanored.vpn.AppConfig
import com.nanored.vpn.R
import com.nanored.vpn.dto.ProfileItem
import com.nanored.vpn.extension.toSpeedString
import com.nanored.vpn.extension.toTrafficString
import com.nanored.vpn.ui.MainActivity
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlin.math.min

object NotificationManager {
    private const val NOTIFICATION_ID = 1
    private const val NOTIFICATION_PENDING_INTENT_CONTENT = 0
    private const val NOTIFICATION_PENDING_INTENT_STOP_V2RAY = 1
    private const val NOTIFICATION_PENDING_INTENT_RESTART_V2RAY = 2
    private const val NOTIFICATION_ICON_THRESHOLD = 3000

    private var lastQueryTime = 0L
    private var mBuilder: NotificationCompat.Builder? = null
    private var speedNotificationJob: Job? = null
    private var mNotificationManager: NotificationManager? = null

    // Session tracking
    private var sessionStartTime = 0L
    private var totalDownloadBytes = 0L
    private var totalUploadBytes = 0L
    private var currentProfileRemarks: String? = null

    /**
     * Starts the speed notification.
     * @param currentConfig The current profile configuration.
     */
    fun startSpeedNotification(currentConfig: ProfileItem?) {
        if (MmkvManager.decodeSettingsBool(AppConfig.PREF_SPEED_ENABLED) != true) return
        if (speedNotificationJob != null || V2RayServiceManager.isRunning() == false) return

        lastQueryTime = System.currentTimeMillis()
        if (sessionStartTime == 0L) {
            sessionStartTime = System.currentTimeMillis()
        }
        var lastZeroSpeed = false
        val outboundTags = currentConfig?.getAllOutboundTags()
        outboundTags?.remove(AppConfig.TAG_DIRECT)
        currentProfileRemarks = currentConfig?.remarks

        speedNotificationJob = CoroutineScope(Dispatchers.IO).launch {
            while (isActive) {
                val queryTime = System.currentTimeMillis()
                val sinceLastQueryInSeconds = (queryTime - lastQueryTime) / 1000.0
                var proxyTotal = 0L
                var currentDownSpeed = 0.0
                var currentUpSpeed = 0.0
                val text = StringBuilder()
                outboundTags?.forEach {
                    val up = V2RayServiceManager.queryStats(it, AppConfig.UPLINK)
                    val down = V2RayServiceManager.queryStats(it, AppConfig.DOWNLINK)
                    if (up + down > 0) {
                        val upSpeed = up / sinceLastQueryInSeconds
                        val downSpeed = down / sinceLastQueryInSeconds
                        appendSpeedString(text, it, upSpeed, downSpeed)
                        proxyTotal += up + down
                        totalDownloadBytes += down
                        totalUploadBytes += up
                        currentDownSpeed += downSpeed
                        currentUpSpeed += upSpeed
                    }
                }
                val directUplink = V2RayServiceManager.queryStats(AppConfig.TAG_DIRECT, AppConfig.UPLINK)
                val directDownlink = V2RayServiceManager.queryStats(AppConfig.TAG_DIRECT, AppConfig.DOWNLINK)
                totalDownloadBytes += directDownlink
                totalUploadBytes += directUplink
                val zeroSpeed = proxyTotal == 0L && directUplink == 0L && directDownlink == 0L
                if (!zeroSpeed || !lastZeroSpeed) {
                    if (proxyTotal == 0L) {
                        appendSpeedString(text, outboundTags?.firstOrNull(), 0.0, 0.0)
                    }
                    val directDownSpeed = directDownlink / sinceLastQueryInSeconds
                    val directUpSpeed = directUplink / sinceLastQueryInSeconds
                    appendSpeedString(text, AppConfig.TAG_DIRECT, directUpSpeed, directDownSpeed)
                    currentDownSpeed += directDownSpeed
                    currentUpSpeed += directUpSpeed

                    // Build compact title for collapsed notification
                    val compactTitle = buildCompactTitle(
                        currentProfileRemarks,
                        currentDownSpeed,
                        totalDownloadBytes + totalUploadBytes,
                        queryTime
                    )

                    // Build detailed text for expanded notification
                    val totalTraffic = totalDownloadBytes + totalUploadBytes
                    val uptimeStr = formatUptime(queryTime - sessionStartTime)
                    text.append("\n")
                    text.append("⏱ $uptimeStr  |  ")
                    text.append("📊 ↓${totalDownloadBytes.toTrafficString()}  ↑${totalUploadBytes.toTrafficString()}  ")
                    text.append("Σ ${totalTraffic.toTrafficString()}")

                    updateNotification(compactTitle, text.toString(), proxyTotal, directDownlink + directUplink)
                }
                lastZeroSpeed = zeroSpeed
                lastQueryTime = queryTime
                delay(3000)
            }
        }
    }

    /**
     * Shows the notification.
     * @param currentConfig The current profile configuration.
     */
    fun showNotification(currentConfig: ProfileItem?) {
        val service = getService() ?: return
        val flags = PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT

        val startMainIntent = Intent(service, MainActivity::class.java)
        val contentPendingIntent = PendingIntent.getActivity(service, NOTIFICATION_PENDING_INTENT_CONTENT, startMainIntent, flags)

        val stopV2RayIntent = Intent(AppConfig.BROADCAST_ACTION_SERVICE)
        stopV2RayIntent.`package` = AppConfig.ANG_PACKAGE
        stopV2RayIntent.putExtra("key", AppConfig.MSG_STATE_STOP)
        val stopV2RayPendingIntent = PendingIntent.getBroadcast(service, NOTIFICATION_PENDING_INTENT_STOP_V2RAY, stopV2RayIntent, flags)

        val restartV2RayIntent = Intent(AppConfig.BROADCAST_ACTION_SERVICE)
        restartV2RayIntent.`package` = AppConfig.ANG_PACKAGE
        restartV2RayIntent.putExtra("key", AppConfig.MSG_STATE_RESTART)
        val restartV2RayPendingIntent = PendingIntent.getBroadcast(service, NOTIFICATION_PENDING_INTENT_RESTART_V2RAY, restartV2RayIntent, flags)

        val channelId =
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                createNotificationChannel()
            } else {
                // If earlier version channel ID is not used
                // https://developer.android.com/reference/android/support/v4/app/NotificationCompat.Builder.html#NotificationCompat.Builder(android.content.Context)
                ""
            }

        // Initialize session tracking
        sessionStartTime = System.currentTimeMillis()
        totalDownloadBytes = 0L
        totalUploadBytes = 0L
        currentProfileRemarks = currentConfig?.remarks

        mBuilder = NotificationCompat.Builder(service, channelId)
            .setSmallIcon(R.drawable.ic_stat_name)
            .setContentTitle(currentConfig?.remarks)
            .setPriority(NotificationCompat.PRIORITY_MIN)
            .setOngoing(true)
            .setShowWhen(false)
            .setOnlyAlertOnce(true)
            .setContentIntent(contentPendingIntent)
            .addAction(
                R.drawable.ic_delete_24dp,
                service.getString(R.string.notification_action_stop_v2ray),
                stopV2RayPendingIntent
            )
            .addAction(
                R.drawable.ic_delete_24dp,
                service.getString(R.string.title_service_restart),
                restartV2RayPendingIntent
            )

        //mBuilder?.setDefaults(NotificationCompat.FLAG_ONLY_ALERT_ONCE)

        service.startForeground(NOTIFICATION_ID, mBuilder?.build())
    }

    /**
     * Cancels the notification.
     */
    fun cancelNotification() {
        val service = getService() ?: return
        service.stopForeground(Service.STOP_FOREGROUND_REMOVE)

        mBuilder = null
        speedNotificationJob?.cancel()
        speedNotificationJob = null
        mNotificationManager = null
        sessionStartTime = 0L
        totalDownloadBytes = 0L
        totalUploadBytes = 0L
        currentProfileRemarks = null
    }

    /**
     * Stops the speed notification.
     * @param currentConfig The current profile configuration.
     */
    fun stopSpeedNotification(currentConfig: ProfileItem?) {
        speedNotificationJob?.let {
            it.cancel()
            speedNotificationJob = null
            updateNotification(currentConfig?.remarks, currentConfig?.remarks, 0, 0)
        }
    }

    /**
     * Creates a notification channel for Android O and above.
     * @return The channel ID.
     */
    @RequiresApi(Build.VERSION_CODES.O)
    private fun createNotificationChannel(): String {
        val channelId = AppConfig.RAY_NG_CHANNEL_ID
        val channelName = AppConfig.RAY_NG_CHANNEL_NAME
        val chan = NotificationChannel(
            channelId,
            channelName, NotificationManager.IMPORTANCE_HIGH
        )
        chan.lightColor = Color.DKGRAY
        chan.importance = NotificationManager.IMPORTANCE_NONE
        chan.lockscreenVisibility = Notification.VISIBILITY_PRIVATE
        getNotificationManager()?.createNotificationChannel(chan)
        return channelId
    }

    /**
     * Builds a compact title string for the collapsed notification view.
     * Format: "ru trojan | 1.2 MB/s↓ | 2.5 GB | 01:25:39"
     *
     * @param remarks The server name/remarks.
     * @param downSpeed The current download speed in bytes/sec.
     * @param totalTraffic The total session traffic in bytes.
     * @param currentTime The current timestamp.
     * @return The formatted compact title string.
     */
    private fun buildCompactTitle(
        remarks: String?,
        downSpeed: Double,
        totalTraffic: Long,
        currentTime: Long
    ): String {
        val name = remarks ?: "VPN"
        val speed = downSpeed.toLong().toSpeedString()
        val traffic = totalTraffic.toTrafficString()
        val uptime = formatUptime(currentTime - sessionStartTime)
        return "$name | ${speed}↓ | $traffic | $uptime"
    }

    /**
     * Formats milliseconds into HH:MM:SS uptime string.
     *
     * @param millis The duration in milliseconds.
     * @return Formatted string like "01:25:39".
     */
    private fun formatUptime(millis: Long): String {
        val totalSeconds = millis / 1000
        val hours = totalSeconds / 3600
        val minutes = (totalSeconds % 3600) / 60
        val seconds = totalSeconds % 60
        return String.format("%02d:%02d:%02d", hours, minutes, seconds)
    }

    /**
     * Updates the notification with the given title, content text and traffic data.
     * @param titleText The title text (compact view).
     * @param contentText The content text (expanded view).
     * @param proxyTraffic The proxy traffic delta.
     * @param directTraffic The direct traffic delta.
     */
    private fun updateNotification(titleText: String?, contentText: String?, proxyTraffic: Long, directTraffic: Long) {
        if (mBuilder != null) {
            if (proxyTraffic < NOTIFICATION_ICON_THRESHOLD && directTraffic < NOTIFICATION_ICON_THRESHOLD) {
                mBuilder?.setSmallIcon(R.drawable.ic_stat_name)
            } else if (proxyTraffic > directTraffic) {
                mBuilder?.setSmallIcon(R.drawable.ic_stat_proxy)
            } else {
                mBuilder?.setSmallIcon(R.drawable.ic_stat_direct)
            }
            mBuilder?.setContentTitle(titleText)
            mBuilder?.setStyle(NotificationCompat.BigTextStyle().bigText(contentText))
            mBuilder?.setContentText(contentText)
            getNotificationManager()?.notify(NOTIFICATION_ID, mBuilder?.build())
        }
    }

    /**
     * Gets the notification manager.
     * @return The notification manager.
     */
    private fun getNotificationManager(): NotificationManager? {
        if (mNotificationManager == null) {
            val service = getService() ?: return null
            mNotificationManager = service.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        }
        return mNotificationManager
    }

    /**
     * Appends the speed string to the given text.
     * @param text The text to append to.
     * @param name The name of the tag.
     * @param up The uplink speed.
     * @param down The downlink speed.
     */
    private fun appendSpeedString(text: StringBuilder, name: String?, up: Double, down: Double) {
        var n = name ?: "no tag"
        n = n.take(min(n.length, 6))
        text.append(n)
        for (i in n.length..6 step 2) {
            text.append("\t")
        }
        text.append("•  ${up.toLong().toSpeedString()}↑  ${down.toLong().toSpeedString()}↓\n")
    }

    /**
     * Gets the service instance.
     * @return The service instance.
     */
    private fun getService(): Service? {
        return V2RayServiceManager.serviceControl?.get()?.getService()
    }
}
