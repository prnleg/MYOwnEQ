# Como lançar uma versão do MyOwnEQ

## Configuração (só uma vez)

1. **Chave de assinatura das atualizações.** Todo arquivo da release é assinado
   com uma chave ed25519. Os apps só instalam atualizações cuja assinatura bate
   com a chave pública em `core/update/update_public_key.txt`.
   - A chave privada fica em `~/.myowneq/update-signing-key` no Mac do mantenedor (meu).
   - Adicione no GitHub: *Settings → Secrets and variables → Actions → New secret*,
     nome `MYOWNEQ_SIGNING_KEY`, valor = conteúdo do arquivo
     (`pbcopy < ~/.myowneq/update-signing-key`).
   - Faça backup offline. Se a chave for perdida, as instalações existentes não
     conseguem se atualizar para builds assinados com uma chave nova (vão precisar
     de uma instalação manual).
   - Nova chave: `cargo run -p myowneq-release -- keygen <arquivo>` (reescreve o
     arquivo da chave pública).

2. **(Opcional) Assinatura estável no macOS.** Sem ela, o CI assina ad-hoc e o
   macOS pede de novo a permissão de captura de áudio a cada atualização. Exporte
   o certificado "Apple Development" pelo Acesso às Chaves como .p12 e adicione os
   secrets `MAC_CERT_P12` (`base64 -i cert.p12 | pbcopy`) e `MAC_CERT_PASSWORD`.

3. **Visibilidade da imagem do servidor.** Depois da primeira release, abra
   *github.com/prnleg?tab=packages → myowneq-server → Package settings* e deixe
   como Public, para o `docker compose pull` funcionar sem login.

## A cada release

```sh
git tag v0.2.0
git push origin v0.2.0
```

O workflow `release` gera o zip/dmg do Mac, o instalador do Windows e a imagem
do servidor, assina tudo, escreve o `latest.json` e publica a release no GitHub.
Os apps abertos detectam em até um dia (ou no *Check now*) e instalam no próximo
reinício.

## Gatekeeper / SmartScreen

Os builds não são notarizados (sem conta paga da Apple) nem assinados com
Authenticode. Só na primeira instalação: macOS → clique direito › Abrir;
Windows → Mais informações › Executar assim mesmo. Atualizações instaladas pelo
próprio app não mostram esses avisos.
