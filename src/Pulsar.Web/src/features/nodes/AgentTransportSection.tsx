import { CircleCheck, ShieldCheck } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { Button, Spinner } from '../../components/ui/Button';
import { Alert, Badge, Card } from '../../components/ui/Display';
import { errorMessage } from '../../lib/errors';
import { useAgentTransport, useSetAgentTransport } from './api';

/**
 * Client certificates for agents (Phase 16): on the panel's agents' address an agent presents its node key in the TLS
 * handshake, so a stolen session token is useless without it. Requiring it sends every agent there; those still
 * connecting without one are listed first.
 */
export function AgentTransportSection() {
  const { t } = useTranslation();
  const overview = useAgentTransport();
  const change = useSetAgentTransport();
  const error = overview.error ?? change.error;
  const data = overview.data;

  // The question counts the nodes as they are now: one may have connected without a certificate since the page loaded.
  const require = async () => {
    const fresh = await overview.refetch();
    if (!fresh.data) {
      return;
    }

    const count = fresh.data.nodesWithoutCertificateCount;
    if (count === 0 || window.confirm(t('agentTransport.requireConfirm', { count }))) {
      change.mutate(true);
    }
  };

  return (
    <section className="mt-10">
      <h2 className="mb-1 text-lg font-semibold">{t('agentTransport.title')}</h2>
      <p className="mb-4 text-sm text-muted">{t('agentTransport.description')}</p>
      {overview.isPending && <Spinner label={t('common.loading')} />}
      {error && <Alert>{errorMessage(t, error)}</Alert>}
      {data && !data.endpointConfigured && (
        <Card className="p-6">
          <p className="text-sm text-muted">{t('agentTransport.noEndpoint')}</p>
          {data.requireClientCertificates && (
            <>
              <p className="mt-2 text-sm text-danger">{t('agentTransport.notInEffect')}</p>
              {/* Saved as required: it would hold again as soon as the endpoint is back. */}
              <Button
                variant="secondary"
                className="mt-4"
                busy={change.isPending}
                onClick={() => window.confirm(t('agentTransport.stopRequiringConfirm')) && change.mutate(false)}
              >
                {t('agentTransport.stopRequiring')}
              </Button>
            </>
          )}
        </Card>
      )}
      {data?.endpointConfigured && (
        <Card className="space-y-4 p-6">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div className="min-w-0 text-sm">
              <p className="text-muted">{t('agentTransport.address')}</p>
              <code className="font-mono text-sm break-all">{data.agentsUrl}</code>
            </div>
            <Badge tone={data.inEffect ? 'success' : 'neutral'}>
              {t(data.inEffect ? 'agentTransport.required' : 'agentTransport.optional')}
            </Badge>
          </div>
          {data.nodesWithoutCertificateCount === 0 ? (
            <p className="flex items-center gap-2 text-sm">
              <CircleCheck className="size-4 shrink-0 text-success" />
              {t('agentTransport.allWithCertificate')}
            </p>
          ) : (
            <div className="text-sm">
              <p>{t('agentTransport.withoutCertificate', { count: data.nodesWithoutCertificateCount })}</p>
              <ul className="mt-2 space-y-1">
                {data.nodesWithoutCertificate.map((node) => (
                  <li key={node.id} className="flex flex-wrap items-center gap-2">
                    <Link to={`/nodes/${node.id}`} className="hover:text-accent hover:underline">
                      {node.name}
                    </Link>
                    <span className="text-xs text-muted">
                      {t(node.connected ? 'agentTransport.connected' : 'agentTransport.offline')}
                    </span>
                    {node.agentCanUseCertificate === false && (
                      <Badge tone="danger">{t('agentTransport.updateAgent')}</Badge>
                    )}
                  </li>
                ))}
              </ul>
              {data.nodesWithoutCertificateCount > data.nodesWithoutCertificate.length && (
                <p className="mt-1 text-xs text-muted">
                  {t('agentTransport.andMore', {
                    count: data.nodesWithoutCertificateCount - data.nodesWithoutCertificate.length,
                  })}
                </p>
              )}
            </div>
          )}
          <p className="text-xs text-muted">{t('agentTransport.requireHint')}</p>
          <div className="flex flex-wrap gap-2">
            {data.requireClientCertificates ? (
              <Button
                variant="secondary"
                busy={change.isPending}
                onClick={() => window.confirm(t('agentTransport.stopRequiringConfirm')) && change.mutate(false)}
              >
                {t('agentTransport.stopRequiring')}
              </Button>
            ) : (
              <Button
                variant="secondary"
                icon={ShieldCheck}
                busy={change.isPending || overview.isFetching}
                onClick={() => void require()}
              >
                {t('agentTransport.require')}
              </Button>
            )}
          </div>
        </Card>
      )}
    </section>
  );
}
