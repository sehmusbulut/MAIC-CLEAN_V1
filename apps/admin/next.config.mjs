const apiOrigin = process.env.MAIC_API_ORIGIN ?? "http://localhost:58543";

/** @type {import("next").NextConfig} */
const nextConfig = {
  async rewrites() {
    return [
      { source: "/auth/:path*", destination: `${apiOrigin}/auth/:path*` },
      { source: "/workspaces/:path*", destination: `${apiOrigin}/workspaces/:path*` },
      { source: "/superadmin/:path*", destination: `${apiOrigin}/superadmin/:path*` },
      { source: "/public/:path*", destination: `${apiOrigin}/public/:path*` },
      { source: "/webhooks/:path*", destination: `${apiOrigin}/webhooks/:path*` },
    ];
  },
};

export default nextConfig;
