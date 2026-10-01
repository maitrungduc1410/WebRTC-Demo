import {
  encryptStream,
  decryptStream,
  deriveFrameKey,
  generateKeyMaterial,
  type CodecMap,
  type FrameTransformOptions,
  type MediaKind,
} from "./e2ee";

let encryptionKey: CryptoKey | undefined;
let codecMap: CodecMap = {};

function transformOptions(kind: MediaKind): FrameTransformOptions {
  return { kind, getKey: () => encryptionKey, getCodecMap: () => codecMap };
}

function startPipeline(operation: "encrypt" | "decrypt", kind: MediaKind, readable: ReadableStream, writable: WritableStream) {
  const pipe = operation === "encrypt" ? encryptStream : decryptStream;
  pipe(transformOptions(kind), readable, writable).catch((error) =>
    console.error(`E2EE ${operation} ${kind} pipeline closed:`, error)
  );
}

// Handle incoming messages from the main thread
self.onmessage = async (event) => {
  const { action, key, readable, writable, kind } = event.data;

  switch (action) {
    case "generateKey": {
      const material = generateKeyMaterial();
      encryptionKey = await deriveFrameKey(material);
      self.postMessage({ action: "generatedKey", key: material });
      break;
    }

    case "setKey":
      encryptionKey = await deriveFrameKey(key);
      console.log("E2EE key set");
      break;

    case "setCodecMap":
      codecMap = event.data.codecMap;
      break;

    case "encrypt":
    case "decrypt":
      startPipeline(action, kind, readable, writable);
      break;
  }
};

// Safari / Firefox: RTCRtpScriptTransform delivers the streams through this event
// @ts-ignore
self.onrtctransform = (event: any) => {
  const { operation, kind } = event.transformer.options;
  startPipeline(operation, kind, event.transformer.readable, event.transformer.writable);
};