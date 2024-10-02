// Encrypt the frame data
export async function encryptStream(
  encryptionKey: CryptoKey | undefined,
  readable: ReadableStream,
  writable: WritableStream
) {
  console.log("Encrypting frame data...", encryptionKey);
  const transformStream = new TransformStream({
    async transform(frame, controller) {
      await encryptFunction(encryptionKey, frame, controller);
    },
  });
  // Pipe the media frames through the transform stream for encryption
  readable.pipeThrough(transformStream).pipeTo(writable);
}

// Decrypt the frame data
export async function decryptStream(
  encryptionKey: CryptoKey | undefined,
  readable: ReadableStream,
  writable: WritableStream
) {
  console.log("Decrypting frame data...", encryptionKey);
  const transformStream = new TransformStream({
    async transform(frame, controller) {
      await decryptFunction(encryptionKey, frame, controller);
    },
  });

  readable.pipeThrough(transformStream).pipeTo(writable);
}

async function encryptFunction(
  encryptionKey: CryptoKey | undefined,
  frame: any,
  controller: any
) {
  if (encryptionKey) {
    const iv = crypto.getRandomValues(new Uint8Array(12)); // Initialization vector for AES-GCM
    const encodedFrame = new Uint8Array(frame.data); // Frame data to encrypt

    // console.log("Original frame data (before encryption):", encodedFrame);

    const encryptedFrame = await crypto.subtle.encrypt(
      {
        name: "AES-GCM",
        iv: iv,
      },
      encryptionKey,
      encodedFrame
    );

    // console.log("Encrypted frame data:", new Uint8Array(encryptedFrame));

    // Append the IV to the encrypted frame (required for decryption later)
    const combined = new Uint8Array(iv.byteLength + encryptedFrame.byteLength);
    combined.set(iv);
    combined.set(new Uint8Array(encryptedFrame), iv.byteLength);

    frame.data = combined.buffer; // Set the new encrypted data
  }
  controller.enqueue(frame);
}

async function decryptFunction(
  encryptionKey: CryptoKey | undefined,
  frame: any,
  controller: any
) {
  if (encryptionKey) {
    const combined = new Uint8Array(frame.data); // Encrypted data with IV
    const iv = combined.slice(0, 12); // Extract the IV
    const encryptedFrame = combined.slice(12); // Extract the encrypted part

    const decryptedFrame = await crypto.subtle.decrypt(
      {
        name: "AES-GCM",
        iv: iv,
      },
      encryptionKey,
      encryptedFrame
    );

    frame.data = decryptedFrame; // Set decrypted data
  }
  controller.enqueue(frame);
}
