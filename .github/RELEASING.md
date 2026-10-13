# 公開手順

GitHubの正式Releaseを公開すると、添付ZIPをThunderstoreの `markn2000/ClassicLegacyUI` へ投稿します。ソースのpush・下書き・プレリリースでは投稿しません。

## 初回設定

1. Thunderstoreの **Settings → Teams → markn2000 → Service Accounts** で投稿用アカウントを作成します。
2. 表示されたトークンを、GitHubリポジトリの **Settings → Secrets and variables → Actions → New repository secret** に `THUNDERSTORE_TOKEN` という名前で登録します。

## 更新時

1. `ClassicLegacyUI.csproj` と `Plugin.cs` のバージョンを上げ、修正をコミット・pushします。
2. 手元で `./Build-Package.ps1` を実行し、Gale・Resoniteで動作を確認します。
3. GitHubでそのコミットのReleaseを下書きし、`dist/ClassicLegacyUI-<version>.zip` を1つ添付してから正式公開します。
4. **Actions → Publish to Thunderstore** の成功とThunderstoreの掲載を確認します。

Releaseのタグ名は自由です。投稿バージョンはZIP内の `manifest.json` から取得します。同じバージョンの再投稿はできないため、公開済み0.2.3をテスト投稿せず、次の更新から利用します。

トークン未設定などで投稿に失敗した場合は、原因を直してGitHub Actionsの **Re-run failed jobs** で再実行できます。通信エラーの場合は、再実行前にThunderstoreへの掲載状況を確認してください。

ZIP検査の変更時は `python checks/Check-PublishWorkflow.py` で、投稿せずに正常系・異常系を確認できます。
