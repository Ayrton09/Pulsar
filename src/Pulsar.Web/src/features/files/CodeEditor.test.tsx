import { EditorView } from '@codemirror/view';
import { render } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import CodeEditor from './CodeEditor';
import { languageFor } from './languages';

// The real CodeMirror (files.test.tsx replaces it with a text area).
describe('code editor', () => {
  const open = (initial: string, lineSeparator?: string) => {
    const onChange = vi.fn();
    const { container } = render(
      <CodeEditor
        label="Contents"
        fileName="server.cfg"
        initial={initial}
        lineSeparator={lineSeparator}
        readOnly={false}
        onChange={onChange}
        onSave={() => {}}
      />,
    );
    const element = container.querySelector<HTMLElement>('.cm-editor');
    const view = element ? EditorView.findFromDOM(element) : null;
    if (!view) {
      throw new Error('no editor');
    }
    return { view, onChange };
  };

  it('reports the text with the line endings the file has', () => {
    const crlf = open('a\r\nb', '\r\n');
    crlf.view.dispatch({ changes: { from: 0, insert: 'x' } });
    expect(crlf.onChange).toHaveBeenLastCalledWith('xa\r\nb');

    const cr = open('a\rb', '\r');
    cr.view.dispatch({ changes: { from: 0, insert: 'x' } });
    expect(cr.onChange).toHaveBeenLastCalledWith('xa\rb');

    const lf = open('a\nb');
    lf.view.dispatch({ changes: { from: 0, insert: 'x' } });
    expect(lf.onChange).toHaveBeenLastCalledWith('xa\nb');
  });
});

describe('syntax highlighting by file name', () => {
  it('knows the extensions it has a language for', () => {
    expect(languageFor('server.cfg')).not.toEqual([]);
    expect(languageFor('SERVER.JSON')).not.toEqual([]);
  });

  it.each(['notes.constructor', 'x.__proto__', 'a.toString', 'a.hasOwnProperty', 'readme', 'archive.tar.gz'])(
    'has no language for %s and does not fail',
    (name) => {
      expect(languageFor(name)).toEqual([]);
    },
  );
});
