import { describe, expect, it } from 'vitest';
import { webLink } from './webLink';

describe('webLink', () => {
  it('keeps web pages', () => {
    expect(webLink('https://letsencrypt.org/repository/')).toBe('https://letsencrypt.org/repository/');
    expect(webLink('http://example.com/a?b=1')).toBe('http://example.com/a?b=1');
  });

  it('refuses anything that is not a web page', () => {
    expect(webLink('javascript:alert(1)')).toBeUndefined();
    expect(webLink(' JavaScript:alert(1)')).toBeUndefined();
    expect(webLink('data:text/html,<script>alert(1)</script>')).toBeUndefined();
    expect(webLink('file:///C:/Windows/win.ini')).toBeUndefined();
    expect(webLink('//evil.example/')).toBeUndefined();
    expect(webLink('not a url')).toBeUndefined();
    expect(webLink('')).toBeUndefined();
    expect(webLink(null)).toBeUndefined();
    expect(webLink(undefined)).toBeUndefined();
  });
});
