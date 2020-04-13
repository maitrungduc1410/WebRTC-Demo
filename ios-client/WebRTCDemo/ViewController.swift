//
//  ViewController.swift
//  WebRTCDemo
//
//  Created by Mai Trung Duc on 10/4/20.
//  Copyright © 2020 Mai Trung Duc. All rights reserved.
//

import UIKit

class ViewController: UIViewController, UITextFieldDelegate {
    @IBOutlet weak var roomIdTextField: UITextField!
    
    override func viewDidLoad() {
        super.viewDidLoad()
        // Do any additional setup after loading the view.
    }
    
    override func viewDidAppear(_ animated: Bool) {
        roomIdTextField.delegate = self
        
        let randomId = Int.random(in: 100000 ... 999999)
        roomIdTextField.text = String(randomId)
    }
    
    @IBAction func randomRoomId(_ sender: Any) {
        let randomId = Int.random(in: 100000 ... 999999)
        roomIdTextField.text = String(randomId)
    }
    
    override func prepare(for segue: UIStoryboardSegue, sender: Any?)
    {
        if segue.destination is CallViewController
        {
            let vc = segue.destination as? CallViewController
            vc?.roomId = roomIdTextField.text!
        }
    }
    
    func textFieldShouldReturn(_ textField: UITextField) -> Bool {
        roomIdTextField.resignFirstResponder()
        return true
    }
}

