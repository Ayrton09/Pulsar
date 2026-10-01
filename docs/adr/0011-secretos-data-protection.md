# ADR-0011 — Secretos cifrados con ASP.NET Core Data Protection

Estado: Propuesta

## Contexto
Hay secretos que el panel debe poder recuperar (GSLT, RCON, clave de firma del panel), no solo verificar.

## Decisión
Cifrado a nivel de aplicación con Data Protection (propósitos separados por tipo de secreto). Key ring persistido en el disco del panel, protegido con DPAPI de máquina en Windows o con certificado (Linux/portabilidad). En el nodo, los specs con secretos se cifran con DPAPI LocalMachine. Las APIs tratan los secretos como write-only.

## Alternativas
- pgcrypto en la BD: la clave viajaría a la BD o estaría en la configuración de conexión.
- Vault/KMS externo: dependencia de infraestructura no justificada en v1 (puede añadirse después como repositorio/protector alternativo del key ring).

## Consecuencias
- Un backup de PostgreSQL sin el key ring no permite descifrar los secretos: la guía de backup debe incluir ambos.
- Con DPAPI de máquina, migrar el panel a otro equipo requiere usar protección por certificado.
