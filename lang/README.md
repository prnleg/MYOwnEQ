# Idiomas

Cada arquivo `.json` aqui é um idioma do MyOwnEQ. A chave é o texto em inglês,
o valor é a tradução. Se faltar alguma tradução, o app mostra o inglês.

## Traduzir para um idioma novo

1. Copie `en-US.json` para `<código>.json` (ex.: `es-ES.json`, `fr-FR.json`).
2. Troque `code`, `name` e `author`.
3. Traduza só os **valores** (lado direito). Não mexa nas chaves.
4. Mantenha os `%@`: são trocados por números/nomes, na mesma ordem.
5. Rode `mac/scripts/lang-keys.py` para ver o que falta e abra um PR.

Para testar sem recompilar: coloque o arquivo em
`~/Library/Application Support/MyOwnEQ/Lang/` e clique em **Reload** na aba
Appearance. Um arquivo seu com um `code` que já existe completa/substitui
o embutido, chave por chave.

## Para quem mexe no código

Todo texto que aparece na tela passa por `L("Texto em inglês")`.
Com valores: `L("Port %@", port)`. Nunca use `\(...)` dentro de `L()`.
Depois rode `mac/scripts/lang-keys.py`: ele atualiza o `en-US.json` e lista o
que falta nos outros idiomas. O CI falha se faltar tradução.
