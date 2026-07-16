export default class StrictReporter {
  skipped = [];

  onTestEnd(test, result) {
    if (result.status === "skipped") {
      this.skipped.push(test.titlePath().join(" > "));
    }
  }

  onEnd() {
    if (this.skipped.length === 0) {
      return undefined;
    }

    console.error(`Unexpected skipped/fixme tests:\n${this.skipped.map((title) => `- ${title}`).join("\n")}`);
    return { status: "failed" };
  }
}
