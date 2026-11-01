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
| 2 | Mac: pega o som do sistema inteiro + app na barra de menu | 🧪 testando |
| 3 | Sync (rede de casa / servidor próprio no Docker / nuvem) | ⏳ em breve |
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
core/ffi     a ponte entre o Rust e os apps nativos
mac/         app SwiftUI na barra de menu + Core Audio Process Tap (macOS 14.2+)
```

## Só compilar 
```sh
cargo test --workspace                               # roda os testes
cargo run -p myowneq-model --example import_eqmac    # mostra teus presets do eqMac
mac/scripts/build-app.sh                             # MyOwnEQ.app universal -> mac/build/
mac/scripts/build-app.sh --native                    # só pra arquitetura do teu Mac, mais rápido
```

> ⚠️ Fecha o eqMac antes de abrir o MyOwnEQ. Dois EQ brigando pelo mesmo som não dá certo.

## Licença
MIT. 

Pega, usa, muda, distribui. Só não sai vendendo o que vc pegou de graça pnc.
