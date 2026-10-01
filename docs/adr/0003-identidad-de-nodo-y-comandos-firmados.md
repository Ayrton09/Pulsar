# ADR-0003 — Identidad por nodo con clave asimétrica, prueba de posesión y comandos firmados

Estado: Propuesta

## Contexto
El agent ejecuta procesos: un fallo en la autenticación panel ↔ agent equivale a ejecución remota de código. Se exige identidad única por nodo, rotación, revocación y protección contra replay. Los proxies que terminan TLS hacen frágil el mTLS.

## Decisión
- Cada nodo genera un par ECDSA P-256 en CNG como clave de máquina no exportable (TPM vía Platform Crypto Provider si está disponible).
- Enrolamiento con token de un solo uso (256 bits, SHA-256 en BD, 30 min) y fijación (pinning) de la clave de firma del panel por fingerprint.
- Cada conexión: challenge con nonce → firma que incluye nodeId, credentialId, nonce y origen del panel → JWT de sesión de 15 min, válido solo para endpoints de agent.
- Todos los comandos panel → agent van en un sobre firmado por el panel con NodeId, SessionId, secuencia (ventana anti-replay), expiración y CommandId.
- Revocación inmediata (estado en BD + abortar conexión); rotación automática a los 90 días.

## Alternativas
- API key por nodo (secreto simétrico): más simple, pero el secreto existe en ambos lados y es exportable.
- mTLS con CA propia: fuerte, pero se rompe detrás de proxies que terminan TLS y exige gestionar CA/CRL. Queda como modo opcional futuro, reutilizando la misma clave.

## Consecuencias
- Robusto aunque TLS o un proxy estén comprometidos.
- Los relojes solo importan como defensa secundaria (la anti-replay principal es por sesión + secuencia).
- Perder la clave de firma del panel obliga a re-enrolar todos los nodos: debe incluirse en los backups del panel.
