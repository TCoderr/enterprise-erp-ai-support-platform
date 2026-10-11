import js from '@eslint/js';
import reactHooks from 'eslint-plugin-react-hooks';
import globals from 'globals';
import tseslint from 'typescript-eslint';

export default tseslint.config(
  { ignores: ['dist', 'node_modules', 'test-results', 'playwright-report'] },
  js.configs.recommended,
  ...tseslint.configs.recommended,
  reactHooks.configs.flat.recommended,
  {
    files: ['**/*.{ts,tsx}'],
    languageOptions: {
      ecmaVersion: 2023,
      sourceType: 'module',
      globals: { ...globals.browser },
    },
    rules: {
      'no-alert': 'error',
      'no-restricted-globals': ['error', 'localStorage', 'sessionStorage', 'indexedDB'],
      'no-restricted-properties': [
        'error',
        { object: 'window', property: 'localStorage', message: 'İstemci verisi tarayıcı deposuna yazılmaz.' },
        { object: 'window', property: 'sessionStorage', message: 'İstemci verisi tarayıcı deposuna yazılmaz.' },
        { object: 'document', property: 'cookie', message: 'Çerezler yalnız sunucu tarafından yazılır.' },
        { property: 'innerHTML', message: 'Metin React öğeleriyle işlenir.' },
      ],
      'no-restricted-syntax': [
        'error',
        { selector: "JSXAttribute[name.name='dangerouslySetInnerHTML']", message: 'Ham HTML işlenmez.' },
      ],
      '@typescript-eslint/consistent-type-imports': ['error', { prefer: 'type-imports' }],
      '@typescript-eslint/no-unused-vars': ['error', { argsIgnorePattern: '^_', varsIgnorePattern: '^_' }],
    },
  },
  {
    // Tests assert that browser storage stays empty, so they may read it.
    files: ['src/**/*.test.{ts,tsx}', 'src/test/**', 'e2e/**/*.ts'],
    rules: { 'no-restricted-properties': 'off', 'no-restricted-globals': 'off' },
  },
  {
    files: ['*.config.{js,ts}', 'e2e/**/*.ts'],
    languageOptions: { globals: { ...globals.node } },
  },
);
