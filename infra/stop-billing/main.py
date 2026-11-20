"""Apaga la facturación del proyecto cuando el gasto alcanza el presupuesto.

Google Cloud Budgets publica en Pub/Sub el gasto del mes varias veces al día. Si el gasto
(costAmount) llega al presupuesto (budgetAmount), se desvincula la cuenta de facturación del
proyecto: todo lo de pago se detiene (Cloud Run incluido) y ya no se generan cargos.
Para reactivar: volver a vincular la cuenta de facturación en la consola.
"""
import base64
import json
import os

import functions_framework
import google.auth
from google.auth.transport.requests import AuthorizedSession

PROJECT_ID = os.environ["TARGET_PROJECT"]
API = f"https://cloudbilling.googleapis.com/v1/projects/{PROJECT_ID}/billingInfo"


@functions_framework.cloud_event
def stop_billing(cloud_event):
    data = json.loads(base64.b64decode(cloud_event.data["message"]["data"]).decode("utf-8"))
    cost, budget = data.get("costAmount", 0), data.get("budgetAmount", 0)
    print(f"Gasto {cost} de {budget} {data.get('currencyCode', '')}")
    if cost < budget:
        return

    credentials, _ = google.auth.default(scopes=["https://www.googleapis.com/auth/cloud-billing"])
    session = AuthorizedSession(credentials)
    info = session.get(API).json()
    if not info.get("billingEnabled"):
        print("La facturación ya estaba desactivada.")
        return

    resp = session.put(API, json={"billingAccountName": ""})
    resp.raise_for_status()
    print(f"Facturación DESACTIVADA en {PROJECT_ID}: el gasto alcanzó el presupuesto.")
