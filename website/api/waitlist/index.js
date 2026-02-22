if (typeof globalThis.crypto === "undefined") {
  globalThis.crypto = require("crypto");
}

module.exports = async function (context, req) {
  const email = req.body && req.body.email;

  if (!email || !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email)) {
    context.res = {
      status: 400,
      headers: { "Content-Type": "application/json" },
      body: { error: "A valid email is required." },
    };
    return;
  }

  try {
    const { TableClient } = require("@azure/data-tables");
    const connectionString = process.env.STORAGE_CONNECTION_STRING;

    if (!connectionString) {
      context.res = {
        status: 500,
        headers: { "Content-Type": "application/json" },
        body: { error: "Storage not configured." },
      };
      return;
    }

    const tableClient = TableClient.fromConnectionString(
      connectionString,
      "waitlist"
    );

    await tableClient.createTable().catch(() => {});

    const clientIpRaw = (req.headers["x-forwarded-for"] || req.headers["x-azure-clientip"] || "")
      .split(",")[0].trim();
    // Strip port suffix (e.g. "209.35.87.94:43381" -> "209.35.87.94")
    const clientIp = clientIpRaw.replace(/:\d+$/, "");
    const ipHash = require("crypto")
      .createHash("sha256")
      .update(clientIp)
      .digest("hex")
      .slice(0, 16);

    // Best-effort geo lookup (free, no key, don't block on failure)
    let country = "";
    let city = "";
    if (clientIp && clientIp !== "127.0.0.1") {
      try {
        const geoRes = await fetch(`http://ip-api.com/json/${clientIp}?fields=country,city,countryCode`);
        if (geoRes.ok) {
          const geo = await geoRes.json();
          country = geo.countryCode || "";
          city = geo.city || "";
        }
      } catch {}
    }

    const entity = {
      partitionKey: "waitlist",
      rowKey: email.toLowerCase(),
      email: email.toLowerCase(),
      signedUpAt: new Date().toISOString(),
      userAgent: (req.headers["user-agent"] || "").slice(0, 500),
      referrer: (req.headers["referer"] || "").slice(0, 500),
      ipHash: ipHash,
      country: country,
      city: city,
    };

    await tableClient.upsertEntity(entity, "Replace");

    context.res = {
      status: 200,
      headers: { "Content-Type": "application/json" },
      body: { message: "Success" },
    };
  } catch (err) {
    context.log.error("Waitlist error:", err.message);
    context.res = {
      status: 500,
      headers: { "Content-Type": "application/json" },
      body: { error: err.message },
    };
  }
};
