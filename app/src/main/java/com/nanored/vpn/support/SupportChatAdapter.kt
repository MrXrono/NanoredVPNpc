package com.nanored.vpn.support

import android.view.Gravity
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.ImageView
import androidx.recyclerview.widget.DiffUtil
import androidx.recyclerview.widget.ListAdapter
import androidx.recyclerview.widget.RecyclerView
import com.nanored.vpn.R
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale

class SupportChatAdapter(
    private val onAttachmentClick: (SupportChatMessage) -> Unit,
) : ListAdapter<SupportChatMessage, SupportChatAdapter.MessageVH>(Diff()) {

    private val timeFormatter = DateTimeFormatter.ofPattern("HH:mm", Locale.getDefault())

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): MessageVH {
        val view = LayoutInflater.from(parent.context).inflate(R.layout.item_support_message, parent, false)
        return MessageVH(view, onAttachmentClick)
    }

    override fun onBindViewHolder(holder: MessageVH, position: Int) {
        holder.bind(getItem(position), timeFormatter)
    }

    class MessageVH(
        itemView: View,
        private val onAttachmentClick: (SupportChatMessage) -> Unit,
    ) : RecyclerView.ViewHolder(itemView) {
        private val container = itemView.findViewById<LinearLayout>(R.id.container)
        private val bubble = itemView.findViewById<LinearLayout>(R.id.bubble)
        private val body = itemView.findViewById<TextView>(R.id.tv_body)
        private val mediaWrap = itemView.findViewById<FrameLayout>(R.id.media_preview_wrap)
        private val photo = itemView.findViewById<ImageView>(R.id.iv_photo)
        private val videoPlay = itemView.findViewById<ImageView>(R.id.iv_video_play)
        private val videoDuration = itemView.findViewById<TextView>(R.id.tv_video_duration)
        private val attachRow = itemView.findViewById<LinearLayout>(R.id.attachment_row)
        private val attachIcon = itemView.findViewById<TextView>(R.id.tv_attachment_icon)
        private val attachName = itemView.findViewById<TextView>(R.id.tv_attachment_name)
        private val meta = itemView.findViewById<TextView>(R.id.tv_meta)

        fun bind(item: SupportChatMessage, timeFormatter: DateTimeFormatter) {
            val isOutgoing = item.direction == SupportDirection.APP_TO_SUPPORT

            val params = bubble.layoutParams as LinearLayout.LayoutParams
            params.gravity = if (isOutgoing) Gravity.END else Gravity.START
            bubble.layoutParams = params

            container.gravity = if (isOutgoing) Gravity.END else Gravity.START
            bubble.setBackgroundResource(
                if (isOutgoing) R.drawable.support_chat_bubble_outgoing else R.drawable.support_chat_bubble_incoming
            )

            if (!item.text.isNullOrBlank()) {
                body.visibility = View.VISIBLE
                body.text = item.text
            } else {
                body.visibility = View.GONE
                body.text = ""
            }

            val isMediaPreview = item.hasAttachment &&
                (item.messageType == SupportMessageType.PHOTO || item.messageType == SupportMessageType.VIDEO)

            if (isMediaPreview) {
                mediaWrap.visibility = View.VISIBLE
                // Guard against view recycling: only set image if tag matches.
                photo.setImageDrawable(null)
                photo.tag = item.id
                val maxW = (photo.resources.displayMetrics.density * 240).toInt()
                val maxH = (photo.resources.displayMetrics.density * 180).toInt()
                val isVideo = item.messageType == SupportMessageType.VIDEO
                videoPlay.visibility = if (isVideo) View.VISIBLE else View.GONE
                videoDuration.visibility = View.GONE
                SupportChatImageLoader.loadPreview(itemView.context, item, maxW, maxH) { preview ->
                    photo.post {
                        if (photo.tag == item.id) {
                            photo.setImageBitmap(preview?.bitmap)
                            if (isVideo && preview?.durationSec != null) {
                                videoDuration.visibility = View.VISIBLE
                                videoDuration.text = formatDuration(preview.durationSec)
                            }
                        }
                    }
                }
                photo.setOnClickListener { onAttachmentClick(item) }
                mediaWrap.setOnClickListener { onAttachmentClick(item) }
            } else {
                mediaWrap.visibility = View.GONE
                photo.setImageDrawable(null)
                photo.setOnClickListener(null)
                mediaWrap.setOnClickListener(null)
                videoPlay.visibility = View.GONE
                videoDuration.visibility = View.GONE
            }

            val showAttachmentRow = item.hasAttachment && !isMediaPreview
            if (showAttachmentRow) {
                attachRow.visibility = View.VISIBLE
                val name = item.fileName?.takeIf { it.isNotBlank() } ?: "attachment"
                attachName.text = name
                attachIcon.text = when (item.messageType) {
                    SupportMessageType.PHOTO -> "\uD83D\uDDBC\uFE0F" // framed picture
                    SupportMessageType.VIDEO -> "\uD83C\uDFA5"      // movie camera
                    SupportMessageType.AUDIO, SupportMessageType.VOICE -> "\uD83C\uDFB5" // musical note
                    SupportMessageType.DOCUMENT, SupportMessageType.FILE -> "\uD83D\uDCC4" // document page
                    else -> "\uD83D\uDCCE"
                }
                attachRow.setOnClickListener { onAttachmentClick(item) }
            } else {
                attachRow.visibility = View.GONE
                attachRow.setOnClickListener(null)
            }

            val timeText = item.createdAtInstant()?.atZone(ZoneId.systemDefault())?.format(timeFormatter) ?: ""
            val who = if (isOutgoing) "Вы" else "Поддержка"
            meta.text = if (item.isPending) "$who • $timeText • отправка..." else "$who • $timeText"
        }

        private fun formatDuration(totalSec: Int): String {
            val sec = totalSec.coerceAtLeast(0)
            val h = sec / 3600
            val m = (sec % 3600) / 60
            val s = sec % 60
            return if (h > 0) {
                String.format(Locale.getDefault(), "%d:%02d:%02d", h, m, s)
            } else {
                String.format(Locale.getDefault(), "%02d:%02d", m, s)
            }
        }
    }

    private class Diff : DiffUtil.ItemCallback<SupportChatMessage>() {
        override fun areItemsTheSame(oldItem: SupportChatMessage, newItem: SupportChatMessage): Boolean =
            oldItem.id == newItem.id

        override fun areContentsTheSame(oldItem: SupportChatMessage, newItem: SupportChatMessage): Boolean =
            oldItem == newItem
    }
}
