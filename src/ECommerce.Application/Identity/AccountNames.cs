using ECommerce.Domain.Customers;
using ECommerce.Domain.Identity;

namespace ECommerce.Application.Identity;

internal static class AccountNames
{
    /// <summary>Nome para a saudação do e-mail: o do cliente, ou a parte antes do @ (admin não tem cadastro de cliente).</summary>
    public static async Task<string> GreetingNameAsync(
        this ICustomerRepository customers, User user, CancellationToken cancellationToken)
    {
        var customer = await customers.GetByIdAsync(user.Id, cancellationToken);
        if (customer is not null)
        {
            return customer.FullName.Split(' ')[0];
        }

        return user.Email.Value[..user.Email.Value.IndexOf('@', StringComparison.Ordinal)];
    }
}
