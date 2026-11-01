# Temas

Cada arquivo `.json` aqui é um tema do MyOwnEQ. Para criar um novo, copie um
deles, troque o `id` (único, minúsculo, com hífen) e as cores, e abra um PR.

Para testar sem recompilar: coloque o arquivo em
`~/Library/Application Support/MyOwnEQ/Themes/` e clique em **Reload** na aba
Appearance. Temas do usuário com o mesmo `id` substituem os embutidos.

| Campo | O que faz |
|---|---|
| `id`, `name`, `author` | Identificação |
| `dark` | `true` = controles do sistema no modo escuro |
| `accent` | Cor de botões, toggles e seleção |
| `curve` | 2+ cores do gradiente da curva de EQ (graves → agudos) |
| `spectrum` | 2 cores do espectro ao vivo (topo → base) |
| `background.base` | Cor de fundo |
| `background.blobs` | Cores das manchas que flutuam no fundo |
| `background.intensity` | 0–1, força das manchas |
| `background.speed` | Velocidade do movimento (1 = normal) |
| `panel` | Cor dos cartões |
| `glow` | 0–1, brilho em volta da curva |

Cores em `#RRGGBB` ou `#RRGGBBAA`.
