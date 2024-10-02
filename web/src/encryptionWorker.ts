import { encryptStream, decryptStream } from "./e2ee";

let encryptionKey: CryptoKey;

// Handle incoming messages from the main thread
self.onmessage = async (event) => {
  const { action, key, readable, writable, shouldSendEncryptionKey } =
    event.data;

  switch (action) {
    case "generateKey":
      await generateKey();
      self.postMessage({ action: "generatedKey", key: encryptionKey });
      break;

    case "setKey":
      await setKey(key);
      break;

    case "encrypt":
      await encryptStream(encryptionKey, readable, writable);
      break;

    case "decrypt":
      // no need to decrypt remote stream if shouldSendEncryptionKey = false, instead just directly display it
      await decryptStream(
        shouldSendEncryptionKey ? encryptionKey : undefined,
        readable,
        writable
      );
      break;
  }
};

// Generate the encryption key (AES-GCM example)
async function generateKey() {
  encryptionKey = await crypto.subtle.generateKey(
    {
      name: "AES-GCM",
      length: 256,
    },
    true,
    ["encrypt", "decrypt"]
  );
}

async function setKey(key: ArrayBuffer) {
  encryptionKey = await crypto.subtle.importKey(
    "raw",
    key,
    { name: "AES-GCM" },
    true,
    ["encrypt", "decrypt"]
  );
  console.log("Key set:", encryptionKey);
}
