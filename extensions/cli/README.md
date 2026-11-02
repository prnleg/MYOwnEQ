# Linha de comando do MyOwnEQ (`myowneq`)

Extensão opcional: controla o app que já está aberto, pelo terminal, por scripts ou
por qualquer programa que rode um comando (Raycast, Atalhos, AutoHotkey, Stream Deck…).

```
myowneq status              o que está tocando (saída, perfil, EQ ligado/desligado)
myowneq list                todos os perfis (* = ativo)
myowneq select <perfil>     troca de perfil (nome, parte do nome ou id)
myowneq on | off | toggle   liga / desliga o EQ
myowneq share <perfil>      mostra o código de compartilhamento (MEQ1-…)
myowneq import <código>     importa um código (use - para ler da entrada padrão)
myowneq open <link>         executa um link myowneq://
--json                      resposta crua em JSON (para scripts)
```

Códigos de saída: `0` ok, `1` erro, `2` app fechado ou controle desligado.

## Instalar

Baixe `myowneq-cli-mac.zip` ou `myowneq-cli-windows.zip` na
[página de releases](https://github.com/prnleg/MYOwnEQ/releases) e coloque o binário
numa pasta do `PATH`.

- **Mac**: `sudo mv myowneq /usr/local/bin/` (ou `~/.local/bin`). O binário não é
  assinado pela Apple; na primeira vez rode `xattr -d com.apple.quarantine myowneq`.
- **Windows**: copie `myowneq.exe` para uma pasta do `PATH` (ex.: `%LOCALAPPDATA%\Programs\MyOwnEQ`).
- Ou compile: `cargo build --release -p myowneq-cli` → `target/release/myowneq`.

No app: **Preferências › Outros apps › "Permitir que outros apps controlem o MyOwnEQ"**
(vem ligado).

## Links `myowneq://`

Funcionam sem a linha de comando (o app registra o link):

| Link | Faz |
|---|---|
| `myowneq://select/Gaming` | troca para o perfil "Gaming" (espaço = `%20`) |
| `myowneq://on`, `off`, `toggle` | liga / desliga o EQ |
| `myowneq://import/MEQ1-…` | importa um código de compartilhamento |

## Receitas

- **Raycast / Alfred (Mac)**: Script Command com `myowneq select "Música"`.
- **Atalhos (Mac)**: ação "Executar script de shell" → `/usr/local/bin/myowneq toggle`.
- **AutoHotkey (Windows)**: `^!g::Run "myowneq.exe select Gaming",, "Hide"`.
- **Stream Deck**: ação *Sistema › Abrir* com `myowneq://select/Gaming` (ou o `.exe` com
  argumentos). Cada botão vira um perfil.

## Como funciona (e segurança)

O app abre uma porta só em `127.0.0.1` (nunca na rede) e grava `control.json` (porta +
chave secreta) na pasta de dados do usuário. A linha de comando lê esse arquivo e manda
um pedido em JSON por linha. Só dá para trocar perfil, ligar/desligar e importar/
compartilhar códigos; nada de apagar ou mexer em sync. Detalhes em
[docs/ARCHITECTURE.md](../../docs/ARCHITECTURE.md#extensões).
