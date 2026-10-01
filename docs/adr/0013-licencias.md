# ADR-0013 — AGPL-3.0 para el core; MIT para manifests y schema

Estado: Propuesta (requiere confirmación del mantenedor)

## Contexto
El core será AGPL-3.0. Los manifests son datos que comunidades y empresas de hosting querrán crear, adaptar y a veces mantener en privado.

## Decisión propuesta
- `src/`, `tests/`, `docs/`: AGPL-3.0 (variante `-only` u `-or-later`, a decidir).
- `games/` y `schemas/`: MIT, para no desincentivar contribuciones de manifests ni crear dudas legales sobre manifests propios.
- Contribuciones con DCO (`Signed-off-by`).

## Consecuencias
- Re-licenciar más adelante exigiría el consentimiento de todos los contribuidores: por eso se decide antes del primer PR externo.
