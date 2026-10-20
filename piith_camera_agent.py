"""Agente de câmera do PIITH: detector local + envio seguro de frames/estado à API.

Variáveis obrigatórias:
  PIITH_API_URL       URL base do site (ex.: https://seu-site.example.com)
  PIITH_CAMERA_TOKEN  mesmo token configurado no servidor
  ROBOFLOW_API_KEY    chave privada do Roboflow
Variáveis opcionais:
  ROBOFLOW_MODEL_ID   padrão: marcelo-valentim-andrade-santana/lab-coat-compliance-1-rfdetr-small-t1
  PIITH_DOCTOR_ID     ID do médico desta sala (padrão 1)
  PIITH_CAMERA_SOURCE índice da webcam (padrão 0)
"""
import os
import time
import threading
from datetime import datetime

import cv2
import requests
import supervision as sv
from inference import get_model

API_URL = os.getenv("PIITH_API_URL", "").rstrip("/")
CAMERA_TOKEN = os.getenv("PIITH_CAMERA_TOKEN", "")
API_KEY = os.getenv("ROBOFLOW_API_KEY", "")
MODEL_ID = os.getenv(
    "ROBOFLOW_MODEL_ID",
    "marcelo-valentim-andrade-santana/lab-coat-compliance-1-rfdetr-small-t1",
)
DOCTOR_ID = int(os.getenv("PIITH_DOCTOR_ID", "1"))
CAMERA_SOURCE = int(os.getenv("PIITH_CAMERA_SOURCE", "0"))
CONFIDENCE = float(os.getenv("PIITH_CONFIDENCE", "0.55"))
INFERENCE_INTERVAL = float(os.getenv("PIITH_INFERENCE_INTERVAL", "0.5"))
UPLOAD_INTERVAL = float(os.getenv("PIITH_UPLOAD_INTERVAL", "0.75"))
CONFIRMATIONS = 2

GREEN = (0, 200, 0)
RED = (0, 0, 220)
ORANGE = (0, 165, 255)
lock = threading.Lock()
stop_event = threading.Event()
shared = {
    "frame": None,
    "present": None,
    "monitoring": False,
    "connected": False,
    "last_upload": 0.0,
}


def open_camera(source):
    if os.name == "nt":
        cam = cv2.VideoCapture(source, cv2.CAP_DSHOW)
        if cam.isOpened():
            return cam
        cam.release()
    return cv2.VideoCapture(source)


def detect(frame, model):
    result = model.infer(frame, confidence=CONFIDENCE)[0]
    detections = sv.Detections.from_inference(result)
    if len(detections) == 0:
        return detections
    return detections[detections.confidence >= CONFIDENCE]


def draw_frame(frame, detections, present, monitoring):
    for box, confidence in zip(detections.xyxy, detections.confidence):
        x1, y1, x2, y2 = (int(v) for v in box)
        cv2.rectangle(frame, (x1, y1), (x2, y2), GREEN, 2)
        cv2.putText(frame, f"jaleco {confidence:.0%}", (x1, max(20, y1 - 8)),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.6, GREEN, 2)
    if not monitoring:
        text, color = "IA PAUSADA: FICHA ATIVA", ORANGE
    elif present is True:
        text, color = "MEDICO PRESENTE", GREEN
    elif present is False:
        text, color = "MEDICO NAO DETECTADO", RED
    else:
        text, color = "AGUARDANDO DETECCAO", ORANGE
    cv2.rectangle(frame, (0, 0), (frame.shape[1], 38), (20, 25, 25), -1)
    cv2.putText(frame, text, (12, 26), cv2.FONT_HERSHEY_SIMPLEX, 0.65, color, 2)
    return frame


def uploader():
    state_url = f"{API_URL}/api/cameras/1/agent/state"
    frame_url = f"{API_URL}/api/cameras/1/agent/frame"
    headers = {"X-Camera-Token": CAMERA_TOKEN}
    last_error = 0.0
    while not stop_event.is_set():
        try:
            response = requests.get(
                state_url, params={"doctorId": DOCTOR_ID}, headers=headers, timeout=4
            )
            response.raise_for_status()
            monitoring = bool(response.json().get("monitoring", False))
            with lock:
                shared["monitoring"] = monitoring
                frame = shared["frame"].copy() if shared["frame"] is not None else None
                present = shared["present"] if monitoring else None
            if frame is not None:
                ok, encoded = cv2.imencode(".jpg", frame, [cv2.IMWRITE_JPEG_QUALITY, 68])
                if ok:
                    payload = {
                        "doctorId": DOCTOR_ID,
                        "medicoPresente": present,
                        "imageBase64": __import__("base64").b64encode(encoded.tobytes()).decode("ascii"),
                    }
                    sent = requests.post(frame_url, json=payload, headers=headers, timeout=5)
                    sent.raise_for_status()
                    with lock:
                        shared["connected"] = True
                        shared["last_upload"] = time.monotonic()
        except Exception as exc:
            with lock:
                shared["connected"] = False
                # Fail-safe: se o servidor não responde, não inferir nem publicar presença nova.
                shared["monitoring"] = False
            if time.monotonic() - last_error > 5:
                print(f"[API PIITH] Sem conexão: {exc}")
                last_error = time.monotonic()
        stop_event.wait(UPLOAD_INTERVAL)


def main():
    if not API_URL or not CAMERA_TOKEN or not API_KEY:
        raise SystemExit(
            "Configure PIITH_API_URL, PIITH_CAMERA_TOKEN e ROBOFLOW_API_KEY antes de executar."
        )

    print("Carregando modelo do Roboflow...")
    model = get_model(model_id=MODEL_ID, api_key=API_KEY)
    camera = open_camera(CAMERA_SOURCE)
    if not camera.isOpened():
        raise SystemExit(f"Não consegui abrir a webcam de índice {CAMERA_SOURCE}.")
    camera.set(cv2.CAP_PROP_FRAME_WIDTH, 640)
    camera.set(cv2.CAP_PROP_FRAME_HEIGHT, 480)
    camera.set(cv2.CAP_PROP_BUFFERSIZE, 1)

    worker = threading.Thread(target=uploader, daemon=True)
    worker.start()
    last_inference = 0.0
    last_state = False
    change_count = 0
    detections = sv.Detections.empty()
    present = None

    print("Agente PIITH ativo. Q fecha a janela; imagem é enviada ao site.")
    try:
        while True:
            ok, frame = camera.read()
            if not ok:
                time.sleep(0.05)
                continue

            now = time.monotonic()
            with lock:
                monitoring = shared["monitoring"]

            if monitoring and now - last_inference >= INFERENCE_INTERVAL:
                last_inference = now
                try:
                    detections = detect(frame, model)
                    detected = len(detections) > 0
                    if detected == last_state:
                        change_count = 0
                    else:
                        change_count += 1
                        if change_count >= CONFIRMATIONS:
                            last_state = detected
                            change_count = 0
                            print(f"[{datetime.now():%H:%M:%S}] medico_presente = {last_state}")
                    present = last_state
                except Exception as exc:
                    print(f"Erro na inferência: {exc}")
            elif not monitoring:
                detections = sv.Detections.empty()
                present = None
                change_count = 0

            preview = draw_frame(frame.copy(), detections, present, monitoring)
            with lock:
                shared["frame"] = preview
                if monitoring:
                    shared["present"] = present
                else:
                    shared["present"] = None
            cv2.imshow("PIITH - Camera 1", preview)
            if cv2.waitKey(1) & 0xFF == ord("q"):
                break
    finally:
        stop_event.set()
        worker.join(timeout=2)
        camera.release()
        cv2.destroyAllWindows()


if __name__ == "__main__":
    main()
