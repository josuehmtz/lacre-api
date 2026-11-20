# Apagado automático de la facturación

Función de Cloud Run functions que recibe los avisos del presupuesto `lacre-tope` (Pub/Sub `billing-alerts`)
y desvincula la cuenta de facturación del proyecto cuando el gasto del mes llega al tope.

Si se activa, la API deja de responder. Para volver: Consola > Facturación > Mis proyectos >
`qr-invitations-515103` > Cambiar facturación (vincular la cuenta otra vez).

Desplegar de nuevo (desde esta carpeta):

```
gcloud functions deploy stop-billing --gen2 --region=us-east1 --runtime=python312 \
  --source=. --entry-point=stop_billing --trigger-topic=billing-alerts \
  --service-account=billing-killswitch@qr-invitations-515103.iam.gserviceaccount.com \
  --set-env-vars=TARGET_PROJECT=qr-invitations-515103 --max-instances=1 --memory=256Mi
```
