# Temas

Jeito fácil: **Preferências › Tema › Personalizar…** no app. Dá para mudar todas as
cores, as manchas do fundo, o brilho e colocar uma **imagem de fundo**, vendo o
resultado ao vivo. Clique direito num tema para editar, compartilhar ou apagar.

**Compartilhar**: cada tema vira um código `MEQT-…` (só as cores e números, a imagem não
vai). Quem recebe cola com **Colar código de tema** (ou no botão de colar da barra
lateral, o mesmo dos perfis), ou abre um link `myowneq://import/MEQT-…`.

Jeito manual: cada arquivo `.json` aqui é um tema do MyOwnEQ. Para criar um novo, copie
um deles, troque o `id` (único, minúsculo, com hífen) e as cores, e abra um PR.

Para testar sem recompilar: coloque o arquivo em
`~/Library/Application Support/MyOwnEQ/Themes/` (Windows: `%APPDATA%\MyOwnEQ\Themes\`)
e clique em **Reload** em Preferências. Temas do usuário com o mesmo `id` substituem os embutidos.

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
| `background.image` | Opcional. Nome do arquivo em `Themes/images/` (`.png`/`.jpg`) |
| `background.image_opacity` | 0–1, força da imagem sobre a cor de fundo (padrão 1) |
| `background.image_blur` | 0–40, desfoque da imagem (padrão 0) |
| `panel` | Cor dos cartões |
| `glow` | 0–1, brilho em volta da curva |

Cores em `#RRGGBB` ou `#RRGGBBAA`.
