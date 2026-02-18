package com.nanored.vpn.support

import android.view.Gravity
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.LinearLayout
import android.widget.TextView
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

            val lines = mutableListOf<String>()
            if (!item.text.isNullOrBlank()) {
                lines += item.text
            }
            if (item.hasAttachment) {
                val name = item.fileName ?: "вложение"
                lines += "📎 $name"
            }

            body.text = lines.joinToString("\n")
            body.setOnClickListener {
                if (item.hasAttachment) onAttachmentClick(item)
            }

            val timeText = item.createdAtInstant()?.atZone(ZoneId.systemDefault())?.format(timeFormatter) ?: ""
            meta.text = if (isOutgoing) "Вы • $timeText" else "Поддержка • $timeText"
        }
    }

    private class Diff : DiffUtil.ItemCallback<SupportChatMessage>() {
        override fun areItemsTheSame(oldItem: SupportChatMessage, newItem: SupportChatMessage): Boolean =
            oldItem.id == newItem.id

        override fun areContentsTheSame(oldItem: SupportChatMessage, newItem: SupportChatMessage): Boolean =
            oldItem == newItem
    }
}
