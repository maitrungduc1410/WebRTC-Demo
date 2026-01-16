//
//  ViewController.swift
//  WebRTCDemo
//
//  Created by Duc Trung Mai on 9/16/24.
//

import UIKit

class ViewController: UIViewController {
    // UI Elements
    private let backgroundImageView = UIImageView()
    private let dimOverlay = UIView()
    private let scrollView = UIScrollView()
    private let contentView = UIView()
    
    private let glassPanelContainer = UIView()
    private let iconContainerView = UIView()
    private let iconImageView = UIImageView()
    private let titleLabel = UILabel()
    private let subtitleLabel = UILabel()
    private let roomIdTextField = UITextField()
    private let joinButton = UIButton(type: .system)
    private let orDividerContainer = UIView()
    private let leftLine = UIView()
    private let orLabel = UILabel()
    private let rightLine = UIView()
    private let randomButton = UIButton(type: .system)
    private let footerLabel = UILabel()
    
    override func viewDidLoad() {
        super.viewDidLoad()
        
        // Hide navigation bar on this screen
        navigationController?.setNavigationBarHidden(true, animated: false)
        
        setupUI()
        generateRandomId()
        
        // Add keyboard observers
        NotificationCenter.default.addObserver(self, selector: #selector(keyboardWillShow(_:)), name: UIResponder.keyboardWillShowNotification, object: nil)
        NotificationCenter.default.addObserver(self, selector: #selector(keyboardWillHide(_:)), name: UIResponder.keyboardWillHideNotification, object: nil)
    }
    
    override func viewWillAppear(_ animated: Bool) {
        super.viewWillAppear(animated)
        navigationController?.setNavigationBarHidden(true, animated: animated)
    }
    
    private func setupUI() {
        view.backgroundColor = UIColor(red: 0.11, green: 0.13, blue: 0.18, alpha: 1.0)
        
        // Background image
        backgroundImageView.image = UIImage(named: "background")
        backgroundImageView.contentMode = .scaleAspectFill
        backgroundImageView.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(backgroundImageView)
        
        // Dim overlay
        dimOverlay.backgroundColor = UIColor.black.withAlphaComponent(0.7)
        dimOverlay.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(dimOverlay)
        
        // Scroll view
        scrollView.translatesAutoresizingMaskIntoConstraints = false
        scrollView.showsVerticalScrollIndicator = false
        view.addSubview(scrollView)
        
        // Content view
        contentView.translatesAutoresizingMaskIntoConstraints = false
        scrollView.addSubview(contentView)
        
        // Glass panel container
        glassPanelContainer.backgroundColor = UIColor(red: 0.06, green: 0.1, blue: 0.13, alpha: 0.75)
        glassPanelContainer.layer.cornerRadius = 24
        glassPanelContainer.layer.borderWidth = 1
        glassPanelContainer.layer.borderColor = UIColor.white.withAlphaComponent(0.1).cgColor
        glassPanelContainer.translatesAutoresizingMaskIntoConstraints = false
        contentView.addSubview(glassPanelContainer)
        
        // Icon container
        iconContainerView.backgroundColor = UIColor(red: 0.24, green: 0.51, blue: 0.96, alpha: 0.2)
        iconContainerView.layer.cornerRadius = 32
        iconContainerView.layer.borderWidth = 1
        iconContainerView.layer.borderColor = UIColor.white.withAlphaComponent(0.1).cgColor
        iconContainerView.translatesAutoresizingMaskIntoConstraints = false
        glassPanelContainer.addSubview(iconContainerView)
        
        // Icon image
        iconImageView.image = UIImage(systemName: "video.fill")
        iconImageView.tintColor = UIColor(red: 0.24, green: 0.51, blue: 0.96, alpha: 1.0)
        iconImageView.contentMode = .center
        iconImageView.translatesAutoresizingMaskIntoConstraints = false
        iconContainerView.addSubview(iconImageView)
        
        // Title
        titleLabel.text = "Join Meeting"
        titleLabel.font = UIFont.systemFont(ofSize: 24, weight: .bold)
        titleLabel.textColor = .white
        titleLabel.textAlignment = .center
        titleLabel.translatesAutoresizingMaskIntoConstraints = false
        glassPanelContainer.addSubview(titleLabel)
        
        // Subtitle
        subtitleLabel.text = "Enter a room number to join an existing call or start a random one."
        subtitleLabel.font = UIFont.systemFont(ofSize: 14)
        subtitleLabel.textColor = UIColor.white.withAlphaComponent(0.6)
        subtitleLabel.textAlignment = .center
        subtitleLabel.numberOfLines = 0
        subtitleLabel.translatesAutoresizingMaskIntoConstraints = false
        glassPanelContainer.addSubview(subtitleLabel)
        
        // Text field
        roomIdTextField.backgroundColor = UIColor.black.withAlphaComponent(0.2)
        roomIdTextField.textColor = .white
        roomIdTextField.font = UIFont.systemFont(ofSize: 18, weight: .semibold)
        roomIdTextField.textAlignment = .center
        roomIdTextField.attributedPlaceholder = NSAttributedString(
            string: "Room Number",
            attributes: [NSAttributedString.Key.foregroundColor: UIColor.white.withAlphaComponent(0.2)]
        )
        roomIdTextField.keyboardType = .numberPad
        roomIdTextField.layer.cornerRadius = 12
        roomIdTextField.layer.borderWidth = 1
        roomIdTextField.layer.borderColor = UIColor.white.withAlphaComponent(0.1).cgColor
        roomIdTextField.delegate = self
        roomIdTextField.translatesAutoresizingMaskIntoConstraints = false
        glassPanelContainer.addSubview(roomIdTextField)
        
        // Join button
        joinButton.setTitle("Join Room", for: .normal)
        joinButton.titleLabel?.font = UIFont.systemFont(ofSize: 16, weight: .bold)
        joinButton.setTitleColor(.white, for: .normal)
        joinButton.backgroundColor = UIColor(red: 0.24, green: 0.51, blue: 0.96, alpha: 1.0)
        joinButton.layer.cornerRadius = 12
        joinButton.addTarget(self, action: #selector(joinButtonTapped), for: .touchUpInside)
        joinButton.translatesAutoresizingMaskIntoConstraints = false
        glassPanelContainer.addSubview(joinButton)
        
        // OR divider
        orDividerContainer.translatesAutoresizingMaskIntoConstraints = false
        glassPanelContainer.addSubview(orDividerContainer)
        
        leftLine.backgroundColor = UIColor.white.withAlphaComponent(0.1)
        leftLine.translatesAutoresizingMaskIntoConstraints = false
        orDividerContainer.addSubview(leftLine)
        
        orLabel.text = "OR"
        orLabel.font = UIFont.systemFont(ofSize: 10, weight: .semibold)
        orLabel.textColor = UIColor.white.withAlphaComponent(0.3)
        orLabel.textAlignment = .center
        orLabel.translatesAutoresizingMaskIntoConstraints = false
        orDividerContainer.addSubview(orLabel)
        
        rightLine.backgroundColor = UIColor.white.withAlphaComponent(0.1)
        rightLine.translatesAutoresizingMaskIntoConstraints = false
        orDividerContainer.addSubview(rightLine)
        
        // Random button
        randomButton.setTitle("  Join Random Room", for: .normal)
        randomButton.setImage(UIImage(systemName: "shuffle"), for: .normal)
        randomButton.titleLabel?.font = UIFont.systemFont(ofSize: 16, weight: .medium)
        randomButton.setTitleColor(.white, for: .normal)
        randomButton.tintColor = UIColor.white.withAlphaComponent(0.6)
        randomButton.backgroundColor = UIColor.white.withAlphaComponent(0.05)
        randomButton.layer.cornerRadius = 12
        randomButton.layer.borderWidth = 1
        randomButton.layer.borderColor = UIColor.white.withAlphaComponent(0.1).cgColor
        randomButton.addTarget(self, action: #selector(randomButtonTapped), for: .touchUpInside)
        randomButton.translatesAutoresizingMaskIntoConstraints = false
        glassPanelContainer.addSubview(randomButton)
        
        // Footer
        footerLabel.text = "WebRTC Demo App"
        footerLabel.font = UIFont.systemFont(ofSize: 12, weight: .medium)
        footerLabel.textColor = UIColor.white.withAlphaComponent(0.2)
        footerLabel.textAlignment = .center
        footerLabel.translatesAutoresizingMaskIntoConstraints = false
        contentView.addSubview(footerLabel)
        
        setupConstraints()
    }
    
    private func setupConstraints() {
        NSLayoutConstraint.activate([
            // Background image
            backgroundImageView.topAnchor.constraint(equalTo: view.topAnchor),
            backgroundImageView.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            backgroundImageView.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            backgroundImageView.bottomAnchor.constraint(equalTo: view.bottomAnchor),
            
            // Dim overlay
            dimOverlay.topAnchor.constraint(equalTo: view.topAnchor),
            dimOverlay.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            dimOverlay.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            dimOverlay.bottomAnchor.constraint(equalTo: view.bottomAnchor),
            
            // Scroll view
            scrollView.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor),
            scrollView.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            scrollView.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            scrollView.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor),
            
            // Content view
            contentView.topAnchor.constraint(equalTo: scrollView.topAnchor),
            contentView.leadingAnchor.constraint(equalTo: scrollView.leadingAnchor),
            contentView.trailingAnchor.constraint(equalTo: scrollView.trailingAnchor),
            contentView.bottomAnchor.constraint(equalTo: scrollView.bottomAnchor),
            contentView.widthAnchor.constraint(equalTo: scrollView.widthAnchor),
            contentView.heightAnchor.constraint(greaterThanOrEqualTo: scrollView.heightAnchor),
            
            // Glass panel container
            glassPanelContainer.centerYAnchor.constraint(equalTo: contentView.centerYAnchor),
            glassPanelContainer.centerXAnchor.constraint(equalTo: contentView.centerXAnchor),
            glassPanelContainer.widthAnchor.constraint(equalToConstant: 350),
            glassPanelContainer.heightAnchor.constraint(greaterThanOrEqualToConstant: 400),
            
            // Icon container
            iconContainerView.topAnchor.constraint(equalTo: glassPanelContainer.topAnchor, constant: 32),
            iconContainerView.centerXAnchor.constraint(equalTo: glassPanelContainer.centerXAnchor),
            iconContainerView.widthAnchor.constraint(equalToConstant: 64),
            iconContainerView.heightAnchor.constraint(equalToConstant: 64),
            
            // Icon image
            iconImageView.centerXAnchor.constraint(equalTo: iconContainerView.centerXAnchor),
            iconImageView.centerYAnchor.constraint(equalTo: iconContainerView.centerYAnchor),
            iconImageView.widthAnchor.constraint(equalToConstant: 32),
            iconImageView.heightAnchor.constraint(equalToConstant: 32),

            // Title
            titleLabel.topAnchor.constraint(equalTo: iconContainerView.bottomAnchor, constant: 16),
            titleLabel.leadingAnchor.constraint(equalTo: glassPanelContainer.leadingAnchor, constant: 32),
            titleLabel.trailingAnchor.constraint(equalTo: glassPanelContainer.trailingAnchor, constant: -32),
            
            // Subtitle
            subtitleLabel.topAnchor.constraint(equalTo: titleLabel.bottomAnchor, constant: 8),
            subtitleLabel.leadingAnchor.constraint(equalTo: glassPanelContainer.leadingAnchor, constant: 32),
            subtitleLabel.trailingAnchor.constraint(equalTo: glassPanelContainer.trailingAnchor, constant: -32),
            
            // Text field
            roomIdTextField.topAnchor.constraint(equalTo: subtitleLabel.bottomAnchor, constant: 32),
            roomIdTextField.leadingAnchor.constraint(equalTo: glassPanelContainer.leadingAnchor, constant: 32),
            roomIdTextField.trailingAnchor.constraint(equalTo: glassPanelContainer.trailingAnchor, constant: -32),
            roomIdTextField.heightAnchor.constraint(equalToConstant: 56),
            
            // Join button
            joinButton.topAnchor.constraint(equalTo: roomIdTextField.bottomAnchor, constant: 12),
            joinButton.leadingAnchor.constraint(equalTo: glassPanelContainer.leadingAnchor, constant: 32),
            joinButton.trailingAnchor.constraint(equalTo: glassPanelContainer.trailingAnchor, constant: -32),
            joinButton.heightAnchor.constraint(equalToConstant: 56),
            
            // OR divider
            orDividerContainer.topAnchor.constraint(equalTo: joinButton.bottomAnchor, constant: 16),
            orDividerContainer.leadingAnchor.constraint(equalTo: glassPanelContainer.leadingAnchor, constant: 32),
            orDividerContainer.trailingAnchor.constraint(equalTo: glassPanelContainer.trailingAnchor, constant: -32),
            orDividerContainer.heightAnchor.constraint(equalToConstant: 20),
            
            leftLine.leadingAnchor.constraint(equalTo: orDividerContainer.leadingAnchor),
            leftLine.centerYAnchor.constraint(equalTo: orDividerContainer.centerYAnchor),
            leftLine.heightAnchor.constraint(equalToConstant: 1),
            leftLine.trailingAnchor.constraint(equalTo: orLabel.leadingAnchor, constant: -12),
            
            orLabel.centerXAnchor.constraint(equalTo: orDividerContainer.centerXAnchor),
            orLabel.centerYAnchor.constraint(equalTo: orDividerContainer.centerYAnchor),
            
            rightLine.leadingAnchor.constraint(equalTo: orLabel.trailingAnchor, constant: 12),
            rightLine.trailingAnchor.constraint(equalTo: orDividerContainer.trailingAnchor),
            rightLine.centerYAnchor.constraint(equalTo: orDividerContainer.centerYAnchor),
            rightLine.heightAnchor.constraint(equalToConstant: 1),
            
            // Random button
            randomButton.topAnchor.constraint(equalTo: orDividerContainer.bottomAnchor, constant: 12),
            randomButton.leadingAnchor.constraint(equalTo: glassPanelContainer.leadingAnchor, constant: 32),
            randomButton.trailingAnchor.constraint(equalTo: glassPanelContainer.trailingAnchor, constant: -32),
            randomButton.heightAnchor.constraint(equalToConstant: 56),
            randomButton.bottomAnchor.constraint(equalTo: glassPanelContainer.bottomAnchor, constant: -32),
            
            // Footer
            footerLabel.bottomAnchor.constraint(equalTo: contentView.bottomAnchor, constant: -32),
            footerLabel.centerXAnchor.constraint(equalTo: contentView.centerXAnchor)
        ])
    }
    
    private func generateRandomId() {
        let randomId = Int.random(in: 100000...999999)
        roomIdTextField.text = String(randomId)
    }
    
    @objc private func joinButtonTapped() {
        guard let roomId = roomIdTextField.text, !roomId.isEmpty else { return }
        
        roomIdTextField.resignFirstResponder()
        
        let callVC = CallViewController()
        callVC.roomId = roomId
        navigationController?.pushViewController(callVC, animated: true)
    }
    
    @objc private func randomButtonTapped() {
        generateRandomId()
    }
    
    @objc private func keyboardWillShow(_ notification: Notification) {
        guard let keyboardFrame = notification.userInfo?[UIResponder.keyboardFrameEndUserInfoKey] as? CGRect else { return }
        let keyboardHeight = keyboardFrame.height
        
        scrollView.contentInset = UIEdgeInsets(top: 0, left: 0, bottom: keyboardHeight, right: 0)
        scrollView.scrollIndicatorInsets = scrollView.contentInset
    }
    
    @objc private func keyboardWillHide(_ notification: Notification) {
        scrollView.contentInset = .zero
        scrollView.scrollIndicatorInsets = .zero
    }
    
    deinit {
        NotificationCenter.default.removeObserver(self)
    }
}

extension ViewController: UITextFieldDelegate {
    func textFieldShouldReturn(_ textField: UITextField) -> Bool {
        textField.resignFirstResponder()
        return true
    }
}
