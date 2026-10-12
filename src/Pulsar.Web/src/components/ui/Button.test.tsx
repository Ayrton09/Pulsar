import { render, screen } from '@testing-library/react';
import { Play } from 'lucide-react';
import { describe, expect, it } from 'vitest';
import { Button } from './Button';

describe('Button', () => {
  it('drops the fill of its variant while it cannot be used, so it does not look clickable', () => {
    render(
      <>
        <Button>Ready</Button>
        <Button disabled>Not now</Button>
        <Button variant="danger" disabled>
          Not now either
        </Button>
      </>,
    );

    expect(screen.getByRole('button', { name: 'Ready' })).toHaveClass('bg-accent');
    expect(screen.getByRole('button', { name: 'Not now' })).not.toHaveClass('bg-accent');
    expect(screen.getByRole('button', { name: 'Not now either' })).not.toHaveClass('bg-danger');
  });

  it('keeps its look while its action runs, with a spinner in place of its icon', () => {
    render(
      <Button icon={Play} busy>
        Start
      </Button>,
    );

    const button = screen.getByRole('button', { name: 'Start' });
    expect(button).toBeDisabled();
    expect(button).toHaveClass('bg-accent');
    expect(button).toHaveAttribute('aria-busy', 'true');
    expect(button.querySelector('svg.lucide-play')).toBeNull();
  });

  it('shows its icon without adding to its name', () => {
    render(<Button icon={Play}>Start</Button>);

    const button = screen.getByRole('button', { name: 'Start' });
    expect(button.querySelector('svg.lucide-play')).toHaveAttribute('aria-hidden', 'true');
  });
});
