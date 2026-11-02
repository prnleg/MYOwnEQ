# Correção de sala / caixa com o REW

O [REW (Room EQ Wizard)](https://www.roomeqwizard.com/) é grátis: você mede a caixa de som
(ou o fone) com um microfone e ele calcula os filtros que deixam o som mais plano. O
MyOwnEQ importa esses filtros direto.

## Passo a passo

1. Meça no REW (de preferência com um microfone calibrado, tipo UMIK-1).
2. Em **EQ**, escolha o equalizador **Generic** (ou **Equalizer APO**) e clique em
   **Match response to target** para gerar os filtros.
3. Exporte: **File › Export › Export filter settings as text**. Sai um `.txt` que começa
   com `Filter Settings file`.
4. No MyOwnEQ:
   - **Como correção** (fica por baixo do seu timbre, como o AutoEQ): no modo Easy, card
     *Correção do fone* › **Importar AutoEQ…** e escolha o `.txt`. Aparece como "REW: …".
   - **Como perfil** (bandas editáveis no modo Pro): no painel da barra de menus / bandeja,
     **Importar › Configuração do Equalizer APO / Peace…** e escolha o `.txt`.

## Dicas

- O cabeçalho do REW (versão, data, notas) é ignorado; filtros `None` também.
- Filtros `OFF` entram desligados, dá pra ligar depois no modo Pro.
- Correção de sala vale para **um lugar** de escuta. Se mudar a caixa ou a cadeira de
  lugar, meça de novo.
- Prefira cortar (ganho negativo) a reforçar: picos da sala se corrigem bem, buracos não.
