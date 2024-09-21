package com.example.myapplication;

import org.webrtc.SdpObserver;
import org.webrtc.SessionDescription;

public class SdpObserverAdapter implements SdpObserver {

    @Override
    public void onCreateSuccess(SessionDescription sessionDescription) {
        // Handle success of creating SDP
        System.out.println("SdpObserverAdapter 11111onCreateSuccess: " + sessionDescription.type + " " + sessionDescription.description);
    }

    @Override
    public void onSetSuccess() {
        // Handle success of setting SDP
        System.out.println("SdpObserverAdapter 11111onSetSuccess");
    }

    @Override
    public void onCreateFailure(String s) {
        // Handle failure of creating SDP
        System.out.println("SdpObserverAdapter 11111onCreateFailure: " + s);
    }

    @Override
    public void onSetFailure(String s) {
        // Handle failure of setting SDP
        System.out.println("SdpObserverAdapter 11111onSetFailure: " + s);
    }
}