package com.example.webrtcdemoandroid;

import androidx.appcompat.app.AppCompatActivity;

import android.content.Intent;
import android.os.Bundle;
import android.text.Editable;
import android.text.TextWatcher;
import android.view.View;
import android.widget.Button;
import android.widget.EditText;

import java.util.Random;

public class MainActivity extends AppCompatActivity {
    public static final String EXTRA_MESSAGE = "com.example.webrtcdemoandroid.ROOM_ID";
    private String roomId;

    @Override
    public void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_main);

        Button btnJoin = findViewById(R.id.btnJoin);
        Button btnRandom = findViewById(R.id.btnRandom);
        EditText roomIDText = findViewById(R.id.roomIDText);

        roomId = generateRandomString(100000, 999999);
        roomIDText.setText(roomId);

        btnJoin.setOnClickListener((View v) -> {
            Intent intent = new Intent(this, CallActivity.class);
            intent.putExtra(EXTRA_MESSAGE, roomId);
            startActivity(intent);
        });

        btnRandom.setOnClickListener((View v) -> {
            roomId = generateRandomString(100000, 999999);
            roomIDText.setText(roomId);
        });

        roomIDText.addTextChangedListener(new TextWatcher() {

            @Override
            public void afterTextChanged(Editable s) {}

            @Override
            public void beforeTextChanged(CharSequence s, int start,
                                          int count, int after) {
            }

            @Override
            public void onTextChanged(CharSequence s, int start,
                                      int before, int count) {
                roomId = s.toString();
            }
        });
    }

    private String generateRandomString (int min, int max) {
        int random = new Random().nextInt(max - min + 1);

        return String.valueOf(random);
    }
}
