package com.example.myapplication

import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import com.example.myapplication.call.CallViewModel
import com.example.myapplication.settings.SignalingServer
import com.example.myapplication.ui.lobby.LobbyScreen
import com.example.myapplication.ui.theme.AppTheme

class MainActivity : ComponentActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)

        val context = this
        setContent {
            AppTheme {
                var serverAddress by remember { mutableStateOf(SignalingServer.load(context)) }
                LobbyScreen(
                    serverAddress = serverAddress,
                    defaultServerAddress = SignalingServer.defaultAddress(context),
                    onServerAddressChange = { address ->
                        SignalingServer.save(context, address)
                        serverAddress = address
                    },
                ) { roomId, e2ee ->
                    startActivity(
                        Intent(context, CallActivity::class.java)
                            .putExtra(CallViewModel.EXTRA_ROOM_ID, roomId)
                            .putExtra(CallViewModel.EXTRA_E2EE, e2ee)
                            .putExtra(CallViewModel.EXTRA_SERVER_ADDRESS, serverAddress)
                    )
                }
            }
        }
    }
}
