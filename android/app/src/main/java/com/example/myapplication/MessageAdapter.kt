package com.example.myapplication

import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.TextView
import androidx.recyclerview.widget.RecyclerView
import java.text.SimpleDateFormat
import java.util.*

class MessageAdapter(
    private val messages: MutableList<Message>,
    private val isOverlay: Boolean = false
) : RecyclerView.Adapter<MessageAdapter.MessageViewHolder>() {

    private val timeFormat = SimpleDateFormat("hh:mm a", Locale.getDefault())

    class MessageViewHolder(view: View) : RecyclerView.ViewHolder(view) {
        val messageContainer: View = view.findViewById(R.id.message_container)
        val senderText: TextView = view.findViewById(R.id.message_sender)
        val messageText: TextView = view.findViewById(R.id.message_text)
        val timeText: TextView = view.findViewById(R.id.message_time)
    }

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): MessageViewHolder {
        val view = LayoutInflater.from(parent.context)
            .inflate(R.layout.item_message, parent, false)
        return MessageViewHolder(view)
    }

    override fun onBindViewHolder(holder: MessageViewHolder, position: Int) {
        val message = messages[position]
        
        holder.senderText.text = message.sender
        holder.messageText.text = message.text
        holder.timeText.text = timeFormat.format(Date(message.timestamp))
        
        // Set background opacity for overlay messages only
        holder.messageContainer.alpha = if (isOverlay) 0.7f else 1.0f
    }

    override fun getItemCount() = messages.size
}
