//
//  DarwinNotificationCenter.swift
//  WebRTCDemo
//
//  Darwin notification center for communication between app and broadcast extension
//

import Foundation

enum DarwinNotification: String {
    case broadcastStarted = "iOS_BroadcastStarted"
    case broadcastStopped = "iOS_BroadcastStopped"
}

class DarwinNotificationCenter {
    
    static let shared = DarwinNotificationCenter()
    
    private let notificationCenter: CFNotificationCenter
    
    private init() {
        notificationCenter = CFNotificationCenterGetDarwinNotifyCenter()
    }
    
    func postNotification(_ name: DarwinNotification) {
        CFNotificationCenterPostNotification(
            notificationCenter,
            CFNotificationName(rawValue: name.rawValue as CFString),
            nil,
            nil,
            true
        )
    }
    
    func addObserver(_ observer: AnyObject, 
                    for name: DarwinNotification, 
                    callback: @escaping () -> Void) {
        let notificationName = CFNotificationName(rawValue: name.rawValue as CFString)
        
        let observer = Unmanaged.passRetained(Observer(callback: callback)).toOpaque()
        
        CFNotificationCenterAddObserver(
            notificationCenter,
            observer,
            { (center, observer, name, object, userInfo) in
                guard let observer = observer else { return }
                let observerObj = Unmanaged<Observer>.fromOpaque(observer).takeUnretainedValue()
                observerObj.callback()
            },
            notificationName.rawValue,
            nil,
            .deliverImmediately
        )
    }
    
    func removeObserver(_ observer: AnyObject, for name: DarwinNotification) {
        let notificationName = CFNotificationName(rawValue: name.rawValue as CFString)
        CFNotificationCenterRemoveObserver(
            notificationCenter,
            Unmanaged.passUnretained(observer).toOpaque(),
            notificationName,
            nil
        )
    }
    
    private class Observer {
        let callback: () -> Void
        
        init(callback: @escaping () -> Void) {
            self.callback = callback
        }
    }
}
