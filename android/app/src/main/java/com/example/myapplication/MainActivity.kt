package com.example.myapplication

import android.content.Intent
import android.os.Bundle
import android.text.Editable
import android.text.TextWatcher
import android.view.WindowManager
import android.widget.Button
import android.widget.EditText
import androidx.appcompat.app.AppCompatActivity
import kotlin.random.Random
import androidx.core.graphics.toColorInt

class MainActivity : AppCompatActivity() {
    
    companion object {
        const val EXTRA_MESSAGE = "com.example.webrtcdemoandroid.ROOM_ID"
    }
    
    private var roomId: String = ""

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // Hide the action bar
        supportActionBar?.hide()

        // Set status bar color
        window.apply {
            addFlags(WindowManager.LayoutParams.FLAG_DRAWS_SYSTEM_BAR_BACKGROUNDS)
            statusBarColor = "#373f3d".toColorInt()
        }

        setContentView(R.layout.activity_main)

        val btnJoin = findViewById<Button>(R.id.btnJoin)
        val btnRandom = findViewById<Button>(R.id.btnRandom)
        val roomIDText = findViewById<EditText>(R.id.roomIDText)

        roomId = generateRandomString(100000, 999999)
        roomIDText.setText(roomId)

        btnJoin.setOnClickListener {
            val intent = Intent(this, CallActivity::class.java).apply {
                putExtra(EXTRA_MESSAGE, roomId)
            }
            startActivity(intent)
        }

        btnRandom.setOnClickListener {
            roomId = generateRandomString(100000, 999999)
            roomIDText.setText(roomId)
        }

        roomIDText.addTextChangedListener(object : TextWatcher {
            override fun afterTextChanged(s: Editable?) {}

            override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) {}

            override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) {
                roomId = s.toString()
            }
        })
    }

    private fun generateRandomString(min: Int, max: Int): String {
        val random = Random.nextInt(min, max + 1)
        return random.toString()
    }
}
