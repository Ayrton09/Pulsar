import { defaultKeymap, history, historyKeymap, indentWithTab } from '@codemirror/commands';
import { bracketMatching, defaultHighlightStyle, HighlightStyle, syntaxHighlighting } from '@codemirror/language';
import { highlightSelectionMatches, search, searchKeymap } from '@codemirror/search';
import { EditorState } from '@codemirror/state';
import {
  drawSelection,
  EditorView,
  highlightActiveLine,
  highlightActiveLineGutter,
  keymap,
  lineNumbers,
} from '@codemirror/view';
import { tags } from '@lezer/highlight';
import { useEffect, useRef } from 'react';
import { languageFor } from './languages';

// Colors from the panel's tokens, so the editor follows the light and dark themes.
const theme = EditorView.theme({
  '&': {
    backgroundColor: 'var(--pulsar-bg)',
    color: 'var(--pulsar-text)',
    fontSize: '13px',
    height: '100%',
  },
  '.cm-scroller': { fontFamily: 'ui-monospace, SFMono-Regular, Consolas, "Liberation Mono", monospace' },
  '.cm-content': { caretColor: 'var(--pulsar-accent)' },
  '.cm-gutters': {
    backgroundColor: 'var(--pulsar-surface)',
    color: 'var(--pulsar-muted)',
    borderRight: '1px solid var(--pulsar-border)',
  },
  '.cm-activeLine, .cm-activeLineGutter': {
    backgroundColor: 'color-mix(in srgb, var(--pulsar-accent) 8%, transparent)',
  },
  '&.cm-focused .cm-selectionBackground, .cm-selectionBackground': {
    backgroundColor: 'color-mix(in srgb, var(--pulsar-accent) 30%, transparent)',
  },
  '.cm-panels': { backgroundColor: 'var(--pulsar-surface)', color: 'var(--pulsar-text)' },
});

const highlight = HighlightStyle.define([
  { tag: tags.comment, color: 'var(--pulsar-muted)', fontStyle: 'italic' },
  { tag: tags.keyword, color: 'var(--pulsar-accent)' },
  { tag: [tags.string, tags.special(tags.string)], color: 'var(--pulsar-success)' },
  { tag: [tags.number, tags.bool, tags.atom], color: 'var(--pulsar-danger)' },
]);

export interface CodeEditorProps {
  /** Labels the editing area for screen readers. */
  label: string;
  fileName: string;
  initial: string;
  /** The file's line ending when it is not 
 (Windows files end lines with 
: kept as they are). */
  lineSeparator?: string | undefined;
  readOnly: boolean;
  /** The id of the text that says how to leave the editor with the keyboard (Tab indents). */
  describedBy?: string | undefined;
  onChange: (text: string) => void;
  onSave: () => void;
}

/**
 * CodeMirror over the file's text. Uncontrolled: the editor owns the document (a React render per keystroke would
 * fight it); the page only hears about changes and Ctrl+S.
 */
export default function CodeEditor({
  label,
  fileName,
  initial,
  lineSeparator,
  readOnly,
  describedBy,
  onChange,
  onSave,
}: CodeEditorProps) {
  const container = useRef<HTMLDivElement>(null);
  const handlers = useRef({ onChange, onSave });
  useEffect(() => {
    handlers.current = { onChange, onSave };
  });

  useEffect(() => {
    const parent = container.current;
    if (!parent) {
      return;
    }
    const view = new EditorView({
      parent,
      state: EditorState.create({
        doc: initial,
        extensions: [
          lineNumbers(),
          highlightActiveLineGutter(),
          history(),
          drawSelection(),
          bracketMatching(),
          highlightActiveLine(),
          highlightSelectionMatches(),
          search({ top: true }),
          syntaxHighlighting(highlight),
          syntaxHighlighting(defaultHighlightStyle, { fallback: true }),
          languageFor(fileName),
          theme,
          lineSeparator ? EditorState.lineSeparator.of(lineSeparator) : [],
          EditorState.readOnly.of(readOnly),
          EditorView.contentAttributes.of(
            describedBy ? { 'aria-label': label, 'aria-describedby': describedBy } : { 'aria-label': label },
          ),
          keymap.of([
            {
              key: 'Mod-s',
              preventDefault: true,
              run: () => {
                handlers.current.onSave();
                return true;
              },
            },
            indentWithTab,
            ...defaultKeymap,
            ...historyKeymap,
            ...searchKeymap,
          ]),
          EditorView.updateListener.of((update) => {
            if (update.docChanged) {
              handlers.current.onChange(update.state.doc.toString());
            }
          }),
        ],
      }),
    });
    return () => view.destroy();
    // The document is the editor's from here on; a new file mounts a new editor (keyed by the page).
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return <div ref={container} className="h-[60vh] min-h-64 overflow-hidden rounded-md border border-border" />;
}
