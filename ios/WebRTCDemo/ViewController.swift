//
//  ViewController.swift
//  WebRTCDemo
//
//  Created by Duc Trung Mai on 9/16/24.
//

import UIKit

class ViewController: UIViewController, UITextFieldDelegate {
    @IBOutlet weak var roomIdTextField: UITextField!
    
    override func viewDidLoad() {
        super.viewDidLoad()
        // Do any additional setup after loading the view.
        
        roomIdTextField.delegate = self
        randomRoomId()
    }
    
    @IBAction func randomRoomId() {
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

