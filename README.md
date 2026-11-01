# MYOwnEQ
VAI TOMA NO ** CANSEI DESSES EQ PAGOS ❌🖕

Equalizador **DE GRAÇA**, **CÓDIGO ABERTO**, pro sistema **INTEIRO**, no **Mac** e no **Windows**, e o perfil sincroniza entre os dois sozinho. Mexeu no Mac, o PC já tá igual. Sem assinatura, sem "versão PRO", sem trial de 7 dias. PAROU COM ESSA P***A 🔥

## PRA QUE CRIAR ESSA P***A??? 😤
- Pagar pra mexer no GRAVE do **MEU** fone? 🤡
- Um app no Mac, outro no Windows, e não se conversam???? 🙄
- Configurar tudo duas vezes, em dois lugares, em dois apps feios? VSF 🚫

Então eu fiz o meu. E se vc quiser é teu tbm 🤝

## Onde estamos no caminho 🗺️
| Fase | O quê | Status |
|---|---|---|
| 1 | O cérebro em Rust: EQ, perfis, fones, regra por app, importadores | ✅ FEITO |
| 2 | Mac: pega o som do sistema inteiro + app na barra de menu | ✅ FEITO |
| 3 | Sync (rede de casa / servidor próprio no Docker / nuvem) | 🧪 testando |
| 4 | Windows (motor do Equalizer APO) | ⏳ em breve |
| 5 | Atualização automática e releases assinadas | ⏳ em breve |

## O que ele faz (ou deveria fazer, calma lá tbm) 💥
- 🎛️ **Modo Pro**: EQ paramétrico e gráfico (10/31 bandas), espectro ao vivo, a banda que vc tá mexendo fica destacada mostrando quanto mudou, limitador, balanço L/R e loudness
- 😎 **Modo Easy (Tetinha)**: Grave / Médio / Clareza / Agudo e pronto. Presets, correção de fone pelo AutoEQ, favoritos só dos fones conectados, tudo pela barra de menu
- 🎧 **Perfil por fone + regra por app**: KZ no padrão, abriu o CS2 → vira **FPS** sozinho 🔫. Fechou o jogo → volta. Momentum 4 nem fica sabendo
- 🔄 **Sync**: liga o "KZ do Mac" com o "KZ do PC" e eles viram um só. Trocou num, trocou no outro
- 📥 **Importa** teus presets do eqMac e do Equalizer APO / Peace. Não perde NADA

## Como é por dentro 🧠
```
core/dsp     filtros em Rust que rodam no thread de áudio sem alocar nem travar
core/model   perfis, fones, regras, salvar/carregar, formatos
core/sync    sync: quem mexeu por último ganha, funciona offline e se acerta depois
core/ffi     a ponte entre o Rust e os apps nativos
server/      servidor de sync (Docker) que roda no teu servidorzinho
mac/         app SwiftUI na barra de menu + Core Audio Process Tap (macOS 14.2+)
```

## Só compilar 
```sh
cargo test --workspace                               # roda os testes
cargo run -p myowneq-model --example import_eqmac    # mostra teus presets do eqMac
mac/scripts/build-app.sh                             # MyOwnEQ.app universal -> mac/build/
mac/scripts/build-app.sh --native                    # só pra arquitetura do teu Mac, mais rápido
```

## Sync 🔄
Nada de precisar criar conta, login, nuvem de empresa nem os krl. Escolhe:

**🏠 Rede de casa**: no Mac vai em *Editor → Sync → Host on this Mac*. Aparece um código tipo `7KQ2-M9XD`. No outro PC: *Connect to a server → Find on my network*, cola o código. Pronto. (Só sincroniza com o host ligado, óbvio)

**🐳 Teu servidor** (o jeito certo):
```sh
cd server
echo "MYOWNEQ_TOKEN=$(openssl rand -hex 16)" > .env
docker compose up -d --build
```
Nos apps: *Connect to a server* → `IP-do-servidor:7878` + o token do `.env`. Vai expor pra internet? Bota atrás de um proxy com HTTPS (Caddy, Nginx, Traefik) e usa `https://teu.dominio`. Sem HTTPS teu token fica exposto (pelado) na rede, não vá cometer um vacilo desse 🤡

**☁️ Nuvem**: mesma imagem Docker, qualquer VPS. Mesma regra do HTTPS.

Depois liga os fones: *Device & Rules → Link with a device from another machine*. Trocou o perfil do KZ no Mac, o KZ do PC troca junto 🤝

> ⚠️ Fecha o eqMac antes de abrir o MyOwnEQ. Dois EQ brigando pelo mesmo som não dá certo.

## Licença
MIT. 

Pega, usa, muda, distribui. Só não sai vendendo o que vc pegou de graça pnc.
